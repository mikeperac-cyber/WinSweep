using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;

namespace WinSweep.Core.Scanning;

/// <summary>
/// Finds setup files (DirectX, Visual C++ redistributables) that uninstalled Steam and Epic
/// games left behind. Nothing is selected by default.
/// </summary>
public static class GameRedistScanner
{
    private static readonly string[] RedistNames = { "_CommonRedist", "DirectX", "vcredist" };
    private static readonly Regex LibraryPath = new("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
    private static readonly Regex InstallDir = new("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);

    /// <summary>Scans the Steam libraries and the Epic launcher on this machine. A missing store contributes nothing.</summary>
    public static IReadOnlyList<CleanupCandidate> Scan()
    {
        var found = new List<CleanupCandidate>();

        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steamPath &&
                steamPath.Length > 0)
            {
                steamPath = steamPath.Replace('/', '\\');
                var libraries = new List<string> { steamPath };
                try
                {
                    var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        libraries.AddRange(ReadSteamLibraries(File.ReadAllText(vdf)));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Use the main Steam folder alone.
                }

                found.AddRange(FindSteamOrphans(libraries.Distinct(StringComparer.OrdinalIgnoreCase)));
            }

        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // Steam contributes nothing when its location cannot be read.
        }

        found.AddRange(FindEpicOrphans(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests")));
        return found;
    }

    /// <summary>Reads the library folders out of Steam's libraryfolders.vdf text.</summary>
    public static IReadOnlyList<string> ReadSteamLibraries(string libraryFoldersVdfText) =>
        LibraryPath.Matches(libraryFoldersVdfText)
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))
            .ToList();

    /// <summary>Finds redistributable folders in Steam game folders that no appmanifest claims.</summary>
    public static IReadOnlyList<CleanupCandidate> FindSteamOrphans(IEnumerable<string> libraryRoots)
    {
        var found = new List<CleanupCandidate>();
        foreach (var root in libraryRoots)
        {
            try
            {
                var steamapps = Path.Combine(root, "steamapps");
                var common = Path.Combine(steamapps, "common");
                if (!Directory.Exists(steamapps) || !Directory.Exists(common))
                {
                    continue;
                }

                var manifests = Directory.GetFiles(steamapps, "appmanifest_*.acf");
                if (manifests.Length == 0)
                {
                    continue; // an unreadable library must not make every game look uninstalled
                }

                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var manifest in manifests)
                {
                    var match = InstallDir.Match(File.ReadAllText(manifest));
                    if (!match.Success)
                    {
                        installed.Clear(); // a manifest we cannot read could be any installed game
                        break;
                    }

                    installed.Add(match.Groups[1].Value);
                }

                if (installed.Count == 0)
                {
                    continue;
                }

                foreach (var game in Directory.GetDirectories(common))
                {
                    if (!installed.Contains(Path.GetFileName(game)))
                    {
                        found.AddRange(FindRedists(game));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Skip a library that cannot be read.
            }
        }

        return found;
    }

    /// <summary>Finds redistributable folders next to Epic games that no launcher manifest claims.</summary>
    public static IReadOnlyList<CleanupCandidate> FindEpicOrphans(string manifestsDirectory)
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(manifestsDirectory))
            {
                return Array.Empty<CleanupCandidate>();
            }

            foreach (var item in Directory.GetFiles(manifestsDirectory, "*.item"))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(item));
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("InstallLocation", out var location) ||
                    location.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(location.GetString()))
                {
                    return Array.Empty<CleanupCandidate>(); // an unreadable manifest must not make a game look orphaned
                }

                installed.Add(Path.GetFullPath(location.GetString()!).TrimEnd('\\'));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            return Array.Empty<CleanupCandidate>();
        }

        var found = new List<CleanupCandidate>();
        foreach (var parent in installed.Select(Path.GetDirectoryName).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (PathGuard.IsRoot(parent!))
                {
                    continue; // never walk a whole drive
                }

                foreach (var game in Directory.GetDirectories(parent!))
                {
                    if (!installed.Contains(game.TrimEnd('\\')))
                    {
                        found.AddRange(FindRedists(game));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // One missing or unreadable drive does not end the scan.
            }
        }

        return found;
    }

    // Looks at the direct children and grandchildren of a game folder; links are never followed.
    private static IEnumerable<CleanupCandidate> FindRedists(string gameFolder)
    {
        var hits = new List<string>();
        if (PathGuard.IsReparsePoint(gameFolder))
        {
            return Array.Empty<CleanupCandidate>();
        }

        try
        {
            foreach (var child in Directory.GetDirectories(gameFolder).Where(d => !PathGuard.IsReparsePoint(d)))
            {
                if (IsRedist(child))
                {
                    hits.Add(child);
                    continue;
                }

                hits.AddRange(Directory.GetDirectories(child).Where(d => IsRedist(d) && !PathGuard.IsReparsePoint(d)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Report what was found so far.
        }

        var name = Path.GetFileName(gameFolder);
        return hits.Select(path => Build(path, name));
    }

    private static bool IsRedist(string directory) =>
        RedistNames.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase);

    private static CleanupCandidate Build(string path, string gameName)
    {
        var size = DirectorySizer.Measure(path, default, TimeSpan.FromSeconds(2));
        DateTime? lastWrite = null;
        try
        {
            lastWrite = Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leave the date unknown.
        }

        return new CleanupCandidate
        {
            Name = $"Leftover installers: {gameName}",
            Path = path,
            Category = "Games",
            Description = $"{gameName} is no longer installed. These are setup files it left behind.",
            Risk = CleanupRisk.Review,
            SizeBytes = size.Bytes,
            SizeStatus = size.Status,
            LastWriteTimeUtc = lastWrite,
            IsSelected = false,
            BlockedReason = PathGuard.Evaluate(path).Allowed
                ? null
                : "Inside a protected system folder. Remove it from the game launcher instead."
        };
    }
}
