using System.Diagnostics;
using System.Text.Json;
using WinSweep.Core.Models;
using WinSweep.Core.Scanning;

namespace WinSweep.Core.Apps;

/// <summary>
/// Lists the Microsoft Store applications installed for the current user.
/// The Windows API is used when the process is packaged; otherwise the same data is
/// read through PowerShell, which works without package identity.
/// </summary>
public sealed class StoreAppInventory
{
    private readonly TimeSpan _sizeBudget;
    private readonly TimeSpan _perAppBudget;

    public StoreAppInventory(TimeSpan? sizeBudget = null, TimeSpan? perAppBudget = null)
    {
        _sizeBudget = sizeBudget ?? TimeSpan.FromSeconds(10);
        _perAppBudget = perAppBudget ?? TimeSpan.FromMilliseconds(800);
    }

    public IReadOnlyList<InstalledApp> ListStoreApps(bool measureSizes = true, CancellationToken cancellationToken = default)
    {
        var apps = TryListWithWindowsApi()
                   ?? TryListWithPowerShell()
                   ?? Array.Empty<InstalledApp>();

        if (!measureSizes)
        {
            return apps;
        }

        // Sizes are measured by the caller under one shared time budget, so a long
        // list of packages cannot slow the whole inventory down.
        return apps;
    }

    /// <summary>
    /// Replaces the raw package id with the name Windows shows for the application,
    /// taken from the Start menu entries.
    /// </summary>
    public static IReadOnlyList<InstalledApp> WithFriendlyNames(IReadOnlyList<InstalledApp> apps)
    {
        var names = GetStartAppNames();
        if (names.Count == 0)
        {
            return apps;
        }

        var results = new List<InstalledApp>(apps.Count);
        foreach (var app in apps)
        {
            string? friendly = null;
            if (app.PackageFamilyName.Length > 0 && names.TryGetValue(app.PackageFamilyName, out var byFamily))
            {
                friendly = byFamily;
            }
            else if (names.TryGetValue(app.Name, out var byName))
            {
                friendly = byName;
            }

            // A packaged component without a Start entry is generally a framework,
            // service, or internal package, not an app a person can launch and remove.
            // Keep those out instead of showing GUIDs or implementation package names.
            if (string.IsNullOrWhiteSpace(friendly))
            {
                continue;
            }

            results.Add(new InstalledApp
            {
                Id = app.Id,
                Name = friendly,
                Version = app.Version,
                Publisher = app.Publisher,
                InstallLocation = app.InstallLocation,
                UninstallString = app.UninstallString,
                QuietUninstallString = app.QuietUninstallString,
                Source = app.Source,
                PackageFamilyName = app.PackageFamilyName,
                SizeBytes = app.SizeBytes
            });
        }

        return results;
    }

    /// <summary>Maps a package family name to the name the Start menu shows.</summary>
    private static Dictionary<string, string> GetStartAppNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return names;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(20_000);

            if (string.IsNullOrWhiteSpace(output))
            {
                return names;
            }

            using var document = JsonDocument.Parse(output);
            var elements = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { document.RootElement };

            foreach (var element in elements)
            {
                var name = GetString(element, "Name");
                var appId = GetString(element, "AppID");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId))
                {
                    continue;
                }

                // A Start entry id looks like "{PackageFamilyName}!{ApplicationId}".
                var separator = appId.IndexOf('!');
                var key = separator > 0 ? appId[..separator] : appId;

                if (!names.ContainsKey(key))
                {
                    names[key] = name;
                }
            }
        }
        catch (Exception e) when (e is JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The raw package id is still shown, which is honest if not pretty.
        }

        return names;
    }

    /// <summary>The folder that holds an app's data, for example under AppData\Packages.</summary>
    public static string GetPackageDataFolder(string packageFamilyName)
    {
        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages");

        return Path.Combine(packages, packageFamilyName);
    }

    private static IReadOnlyList<InstalledApp>? TryListWithWindowsApi()
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();

            IEnumerable<Windows.ApplicationModel.Package> packages;
            try
            {
                packages = manager.FindPackages();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                // An unpackaged process needs the per-user list instead.
                packages = manager.FindPackagesForUser(string.Empty);
            }

            var apps = new List<InstalledApp>();
            foreach (var package in packages)
            {
                var id = package.Id;
                if (id is null)
                {
                    continue;
                }

                var familyName = id.FamilyName;
                if (string.IsNullOrEmpty(familyName))
                {
                    continue;
                }

                // Framework and resource packages are not applications.
                if (id.Name.Contains(".Framework", StringComparison.OrdinalIgnoreCase) ||
                    id.Name.Contains(".Resources", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                apps.Add(new InstalledApp
                {
                    Id = $"package:{id.Name}",
                    Name = string.IsNullOrWhiteSpace(package.DisplayName) ? id.Name : package.DisplayName,
                    Version = $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}",
                    Publisher = string.IsNullOrWhiteSpace(package.PublisherDisplayName) ? id.PublisherId : package.PublisherDisplayName,
                    InstallLocation = package.InstalledLocation?.Path ?? string.Empty,
                    Source = InstallSource.WindowsStore,
                    PackageFamilyName = familyName
                });
            }

            return apps;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<InstalledApp>? TryListWithPowerShell()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command " +
                            "\"Get-AppxPackage | Select-Object Name,PackageFamilyName,Publisher,Version,InstallLocation | ConvertTo-Json -Compress\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            using var document = JsonDocument.Parse(output);
            var elements = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { document.RootElement };

            var apps = new List<InstalledApp>();
            foreach (var element in elements)
            {
                var name = GetString(element, "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var family = GetString(element, "PackageFamilyName");
                var version = GetString(element, "Version");

                apps.Add(new InstalledApp
                {
                    Id = $"package:{name}",
                    Name = name,
                    Version = version,
                    Publisher = GetString(element, "Publisher"),
                    InstallLocation = GetString(element, "InstallLocation"),
                    Source = InstallSource.WindowsStore,
                    PackageFamilyName = family
                });
            }

            return apps;
        }
        catch (Exception e) when (e is JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }
}
