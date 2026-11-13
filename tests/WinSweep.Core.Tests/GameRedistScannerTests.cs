using WinSweep.Core.Models;
using WinSweep.Core.Scanning;
using Xunit;

namespace WinSweep.Core.Tests;

public class GameRedistScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));

    public GameRedistScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ReadsEveryLibraryPathAndUnescapesBackslashes()
    {
        var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\Program Files (x86)\\Steam\"\n\t\t\"label\"\t\t\"\"\n" +
                  "\t\t\"apps\"\n\t\t{\n\t\t\t\"228980\"\t\t\"123\"\n\t\t}\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\SteamLibrary\"\n\t}\n}\n";

        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, GameRedistScanner.ReadSteamLibraries(vdf));
    }

    [Fact]
    public void SteamFlagsRedistOnlyInUninstalledGames()
    {
        var steamapps = Dir("lib", "steamapps");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1.acf"), "\"AppState\"\n{\n\t\"installdir\"\t\t\"Installed Game\"\n}\n");
        Dir("lib", "steamapps", "common", "Installed Game", "_CommonRedist");
        var gone1 = Dir("lib", "steamapps", "common", "Gone Game", "_CommonRedist");
        var gone2 = Dir("lib", "steamapps", "common", "Gone Game", "Engine", "vcredist");
        Dir("lib", "steamapps", "common", "Gone Game", "Binaries");
        Dir("lib", "steamapps", "common", "Empty Leftover", "Data");

        var found = GameRedistScanner.FindSteamOrphans(new[] { Path.Combine(_root, "lib") });

        Assert.Equal(new[] { gone1, gone2 }.OrderBy(p => p), found.Select(c => c.Path).OrderBy(p => p));
        Assert.All(found, c =>
        {
            Assert.False(c.IsSelected);
            Assert.Equal("Games", c.Category);
            Assert.Equal(CleanupRisk.Review, c.Risk);
            Assert.Equal("Leftover installers: Gone Game", c.Name);
        });
    }

    [Fact]
    public void SteamLibraryWithoutManifestsIsIgnored()
    {
        Dir("lib", "steamapps", "common", "Some Game", "_CommonRedist");

        Assert.Empty(GameRedistScanner.FindSteamOrphans(new[] { Path.Combine(_root, "lib") }));
    }

    [Fact]
    public void EpicFlagsRedistInSiblingsOfInstalledGamesOnly()
    {
        var manifests = Dir("manifests");
        var installed = Dir("Epic Games", "Installed");
        Dir("Epic Games", "Installed", "_CommonRedist");
        var orphan = Dir("Epic Games", "Orphan", "DirectX");
        File.WriteAllText(Path.Combine(manifests, "a.item"),
            "{\"InstallLocation\":\"" + installed.Replace("\\", "\\\\") + "\"}");

        var found = GameRedistScanner.FindEpicOrphans(manifests);

        Assert.Equal(orphan, Assert.Single(found).Path);
        Assert.False(found[0].IsSelected);
    }

    [Fact]
    public void EpicWithoutManifestsFindsNothing()
    {
        Dir("Epic Games", "Orphan", "DirectX");

        Assert.Empty(GameRedistScanner.FindEpicOrphans(Dir("manifests")));
    }

    [Fact]
    public void SteamInstalldirMatchIgnoresCase()
    {
        var steamapps = Dir("lib", "steamapps");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1.acf"), "\"installdir\"		\"installed game\"");
        Dir("lib", "steamapps", "common", "Installed Game", "_CommonRedist");

        Assert.Empty(GameRedistScanner.FindSteamOrphans(new[] { Path.Combine(_root, "lib") }));
    }

    [Fact]
    public void SteamLibraryWithAnUnreadableManifestIsIgnored()
    {
        var steamapps = Dir("lib", "steamapps");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1.acf"), "\"installdir\"		\"Good\"");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_2.acf"), "garbage without the key");
        Dir("lib", "steamapps", "common", "Good", "_CommonRedist");
        Dir("lib", "steamapps", "common", "Mystery", "_CommonRedist");

        Assert.Empty(GameRedistScanner.FindSteamOrphans(new[] { Path.Combine(_root, "lib") }));
    }

    [Fact]
    public void EpicWithADamagedOrBlankManifestFindsNothing()
    {
        var manifests = Dir("manifests");
        var installed = Dir("Epic Games", "Installed");
        Dir("Epic Games", "Orphan", "DirectX");
        File.WriteAllText(Path.Combine(manifests, "a.item"), "{\"InstallLocation\":\"" + installed.Replace("\\", "\\\\") + "\"}");
        File.WriteAllText(Path.Combine(manifests, "b.item"), "{ not json");
        Assert.Empty(GameRedistScanner.FindEpicOrphans(manifests));

        File.WriteAllText(Path.Combine(manifests, "b.item"), "{\"InstallLocation\":\"  \"}");
        Assert.Empty(GameRedistScanner.FindEpicOrphans(manifests));
    }

    [Fact]
    public void EpicSkipsMissingParentsAndDriveRoots()
    {
        var manifests = Dir("manifests");
        var good = Dir("Epic Games", "Installed");
        var orphan = Dir("Epic Games", "Orphan", "DirectX");
        var missing = Path.Combine(_root, "NoSuchDrive", "Game");
        var atRoot = Path.Combine(Path.GetPathRoot(_root)!, "WinSweepNoSuchGame");
        var locations = new[] { missing, atRoot, good };
        for (var i = 0; i < locations.Length; i++)
        {
            File.WriteAllText(Path.Combine(manifests, $"{i}.item"),
                "{\"InstallLocation\":\"" + locations[i].Replace("\\", "\\\\") + "\"}");
        }

        Assert.Equal(orphan, Assert.Single(GameRedistScanner.FindEpicOrphans(manifests)).Path);
    }

    [Fact]
    public void ALinkedLibraryIsListedButBlockedAndALinkedGameFolderIsSkipped()
    {
        var real = Dir("real");
        var steamapps = Dir("real", "steamapps");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1.acf"), "\"installdir\"		\"Installed\"");
        Dir("real", "steamapps", "common", "Gone", "_CommonRedist");
        var link = Path.Combine(_root, "link");
        if (!TryJunction(link, real))
        {
            return; // junctions unavailable here; the guard text is then untested
        }

        try
        {
            var found = GameRedistScanner.FindSteamOrphans(new[] { link });

            var candidate = Assert.Single(found);
            Assert.Equal("Inside a protected system folder. Remove it from the game launcher instead.", candidate.BlockedReason);

            // The game folder itself being a link is never entered.
            var target = Dir("target", "_CommonRedist");
            var gameLink = Path.Combine(real, "steamapps", "common", "Linked");
            if (TryJunction(gameLink, Path.GetDirectoryName(target)!))
            {
                try { Assert.DoesNotContain(GameRedistScanner.FindSteamOrphans(new[] { real }), c => c.Path.Contains("Linked")); }
                finally { Directory.Delete(gameLink, false); }
            }
        }
        finally
        {
            Directory.Delete(link, false);
        }
    }

    private static bool TryJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
