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
        try { Directory.Delete(_root, true); } catch (IOException) { }
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
        var games = Dir("Epic Games");
        var installed = Dir("Epic Games", "Installed");
        Dir("Epic Games", "Installed", "_CommonRedist");
        var orphan = Dir("Epic Games", "Orphan", "DirectX");
        File.WriteAllText(Path.Combine(manifests, "a.item"),
            "{\"InstallLocation\":\"" + installed.Replace("\\", "\\\\") + "\"}");

        var found = GameRedistScanner.FindEpicOrphans(manifests);

        Assert.Equal(orphan, Assert.Single(found).Path);
        Assert.False(found[0].IsSelected);
        Assert.NotNull(games);
    }

    [Fact]
    public void EpicWithoutManifestsFindsNothing()
    {
        Dir("Epic Games", "Orphan", "DirectX");

        Assert.Empty(GameRedistScanner.FindEpicOrphans(Dir("manifests")));
    }
}
