using WinSweep.Core.Models;
using WinSweep.Core.Sweep;
using Xunit;

namespace WinSweep.Core.Tests;

public class SweepPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));

    public SweepPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string MakeDir(string name, string fileName = "a.bin")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "x");
        return dir;
    }

    private static RiskAnalyzer NoRisk() => new(Array.Empty<InstalledApp>(), Array.Empty<string>());

    [Fact]
    public void TotalsAndCategoriesAddUp()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        var c = MakeDir("c");

        var manifest = SweepPlanner.Build("clean", new[]
        {
            new SweepTarget(a, "A", "Temp", 100),
            new SweepTarget(b, "B", "Temp", 50),
            new SweepTarget(c, "C", "Cache", 400)
        }, NoRisk(), dryRun: true);

        Assert.Equal(550, manifest.TotalBytes);
        Assert.Equal(150, manifest.BytesByCategory["Temp"]);
        Assert.Equal(400, manifest.BytesByCategory["Cache"]);
        Assert.Equal(new[] { "C", "A", "B" }, manifest.Entries.Select(e => e.Name));
        Assert.True(manifest.DryRun);
        Assert.Matches("^[0-9a-f]{8}$", manifest.Id);
        Assert.All(manifest.Entries, e => Assert.True(e.IsDirectory));
        Assert.All(manifest.Entries, e => Assert.NotNull(e.LastWriteUtc));
    }

    [Fact]
    public void MissingTargetIsLeftOut()
    {
        var a = MakeDir("a");

        var manifest = SweepPlanner.Build("clean", new[]
        {
            new SweepTarget(a, "A", "Temp", 10),
            new SweepTarget(Path.Combine(_root, "gone"), "Gone", "Temp", 99)
        }, NoRisk(), dryRun: false, id: "0123abcd");

        var entry = Assert.Single(manifest.Entries);
        Assert.Equal("A", entry.Name);
        Assert.Equal(10, manifest.TotalBytes);
        Assert.Equal("0123abcd", manifest.Id);
    }

    [Fact]
    public void RiskFollowsTheRulesAndLockedWins()
    {
        var running = MakeDir("running");
        var installed = MakeDir("installed");
        var both = MakeDir("both");
        var plain = MakeDir("plain");
        var apps = new[]
        {
            new InstalledApp { Id = "registry:1", Name = "Demo", InstallLocation = installed },
            new InstalledApp { Id = "registry:2", Name = "Both", InstallLocation = both }
        };
        var analyzer = new RiskAnalyzer(apps, new[]
        {
            Path.Combine(running, "tool.exe"),
            Path.Combine(both, "both.exe")
        });

        var manifest = SweepPlanner.Build("purge", new[]
        {
            new SweepTarget(running, "running", "x", 1),
            new SweepTarget(installed, "installed", "x", 1),
            new SweepTarget(both, "both", "x", 1),
            new SweepTarget(plain, "plain", "x", 1)
        }, analyzer, dryRun: true);

        SweepEntry Get(string n) => manifest.Entries.Single(e => e.Name == n);
        Assert.Equal(SweepRisk.Locked, Get("running").Risk);
        Assert.Equal("tool.exe is running from this location.", Get("running").RiskReason);
        Assert.Equal(SweepRisk.Caution, Get("installed").Risk);
        Assert.Equal("Inside the install folder of Demo.", Get("installed").RiskReason);
        Assert.Equal(SweepRisk.Locked, Get("both").Risk);
        Assert.Equal(SweepRisk.Safe, Get("plain").Risk);
        Assert.Equal(string.Empty, Get("plain").RiskReason);
    }

    [Fact]
    public void SaveThenLoadRoundTripsEverything()
    {
        var manifest = new SweepManifest
        {
            Id = "deadbeef",
            Source = "clean",
            DryRun = true,
            TotalBytes = 42,
            BytesByCategory = { ["Temp"] = 42 },
            Snapshot = new SnapshotInfo(true, false, "not elevated"),
            CreatedAt = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(2))
        };
        manifest.Entries.Add(new SweepEntry
        {
            Path = @"C:\x\y",
            Name = "y",
            Category = "Temp",
            IsDirectory = true,
            SizeBytes = 42,
            LastWriteUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Risk = SweepRisk.Caution,
            RiskReason = "why",
            Status = SweepEntryStatus.Quarantined,
            Archive = "0001.wsq",
            Sha256 = "ab12",
            AclSddl = "D:(A;;FA;;;WD)",
            FilesArchived = 3,
            SkippedFiles = { @"C:\x\y\locked.dat" },
            Detail = "ok"
        });
        var path = Path.Combine(_root, "nested", "sweep-manifest.json");

        manifest.Save(path);
        var text = File.ReadAllText(path);
        var loaded = SweepManifest.Load(path)!;

        Assert.Contains("\"Caution\"", text);
        Assert.Contains("\"Quarantined\"", text);
        Assert.Equal(manifest.Id, loaded.Id);
        Assert.Equal(manifest.Source, loaded.Source);
        Assert.Equal(manifest.CreatedAt, loaded.CreatedAt);
        Assert.True(loaded.DryRun);
        Assert.Equal(42, loaded.TotalBytes);
        Assert.Equal(42, loaded.BytesByCategory["Temp"]);
        Assert.Empty(loaded.RegistryKeys);
        Assert.Equal(new SnapshotInfo(true, false, "not elevated"), loaded.Snapshot);
        var e = Assert.Single(loaded.Entries);
        var o = manifest.Entries[0];
        Assert.Equal(o.Path, e.Path);
        Assert.Equal(o.Name, e.Name);
        Assert.Equal(o.Category, e.Category);
        Assert.True(e.IsDirectory);
        Assert.Equal(o.SizeBytes, e.SizeBytes);
        Assert.Equal(o.LastWriteUtc, e.LastWriteUtc);
        Assert.Equal(o.Risk, e.Risk);
        Assert.Equal(o.RiskReason, e.RiskReason);
        Assert.Equal(o.Status, e.Status);
        Assert.Equal(o.Archive, e.Archive);
        Assert.Equal(o.Sha256, e.Sha256);
        Assert.Equal(o.AclSddl, e.AclSddl);
        Assert.Equal(3, e.FilesArchived);
        Assert.Equal(o.SkippedFiles, e.SkippedFiles);
        Assert.Equal("ok", e.Detail);
    }

    [Fact]
    public void SaveOverAnExistingManifestLeavesNoTempFile()
    {
        var path = Path.Combine(_root, "m", "sweep-manifest.json");
        new SweepManifest { Id = "aaaaaaaa", Source = "clean" }.Save(path);
        new SweepManifest { Id = "bbbbbbbb", Source = "clean" }.Save(path);

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("bbbbbbbb", SweepManifest.Load(path)!.Id);
    }

    [Fact]
    public void LoadReturnsNullForMissingOrDamagedFile()
    {
        Assert.Null(SweepManifest.Load(Path.Combine(_root, "none.json")));
        var bad = Path.Combine(_root, "bad.json");
        File.WriteAllText(bad, "{ not json");
        Assert.Null(SweepManifest.Load(bad));
    }

    [Fact]
    public void BuildLeavesTheTargetUntouched()
    {
        var dir = MakeDir("t", "keep.txt.bin");
        File.WriteAllText(Path.Combine(dir, "other.bin"), "yy");
        string[] Listing() => Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Select(p => $"{p}|{(File.Exists(p) ? new FileInfo(p).Length : -1)}|{File.GetLastWriteTimeUtc(p).Ticks}")
            .OrderBy(s => s).ToArray();
        var before = Listing();

        SweepPlanner.Build("clean", new[] { new SweepTarget(dir, "t", "x", 3) }, NoRisk(), dryRun: true);

        Assert.Equal(before, Listing());
    }
}
