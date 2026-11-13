using WinSweep.Core.Cleanup;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Settings;
using WinSweep.Core.Sweep;
using Xunit;

namespace WinSweep.Core.Tests;

public class SweepRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dryRunPath;
    private readonly OperationLog _log;
    private readonly AppSettings _settings = new();

    public SweepRunnerTests()
    {
        Directory.CreateDirectory(_root);
        _dryRunPath = Path.Combine(_root, "dry", "sweep-manifest.json");
        _log = new OperationLog(Path.Combine(_root, "operations.jsonl"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private SweepRunner NewRunner(string? vaultRoot = null) => new(
        _log, _settings, new QuarantineVault(vaultRoot ?? Path.Combine(_root, "vault")),
        isElevated: () => false,
        analyzer: new RiskAnalyzer(Array.Empty<InstalledApp>(), Array.Empty<string>()),
        dryRunManifestPath: _dryRunPath);

    private string MakeCache(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha " + name);
        File.WriteAllBytes(Path.Combine(dir, "sub", "b.bin"), Enumerable.Range(0, 3000).Select(i => (byte)(i * 3)).ToArray());
        return dir;
    }

    private static SweepTarget Target(string dir) => new(dir, Path.GetFileName(dir), "Temp", 100);

    private static CleanupCandidate Candidate(string dir) => new()
    {
        Name = Path.GetFileName(dir),
        Path = dir,
        Category = "Temp",
        Description = "test fixture",
        SizeBytes = 100,
        SizeStatus = ScanStatus.Complete,
        IsSelected = true
    };

    [Fact]
    public void PreviewWritesADryRunManifestAndTouchesNothing()
    {
        var dir = MakeCache("cache");

        var manifest = NewRunner().Preview("clean", new[] { Target(dir) });

        Assert.True(manifest.DryRun);
        var saved = SweepManifest.Load(_dryRunPath)!;
        Assert.True(saved.DryRun);
        Assert.Equal(manifest.Id, saved.Id);
        Assert.Equal(dir, saved.Entries.Single().Path);
        Assert.True(File.Exists(Path.Combine(dir, "a.txt")));
        Assert.Empty(_log.Read());
    }

    [Fact]
    public async Task BeginReturnsNullWhenTheVaultIsSwitchedOff()
    {
        _settings.UseQuarantineVault = false;

        Assert.Null(await NewRunner().BeginAsync("clean", new[] { Target(MakeCache("cache")) }));
        Assert.False(Directory.Exists(Path.Combine(_root, "vault")));
    }

    [Fact]
    public async Task NotElevatedSweepRecordsThatNoSnapshotWasTaken()
    {
        var session = await NewRunner().BeginAsync("clean", new[] { Target(MakeCache("cache")) });

        var snapshot = session!.Manifest.Snapshot!;
        Assert.False(snapshot.Attempted);
        Assert.False(snapshot.Succeeded);
        Assert.Equal(VolumeSnapshot.NotElevatedMessage, snapshot.Message);
    }

    [Fact]
    public async Task SnapshotIsSkippedWhenSwitchedOff()
    {
        _settings.SnapshotBeforeSweep = false;

        var session = await NewRunner().BeginAsync("clean", new[] { Target(MakeCache("cache")) });

        Assert.Null(session!.Manifest.Snapshot);
    }

    [Fact]
    public async Task VaultThatCannotBeOpenedBecomesOneIOException()
    {
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "x");

        var e = await Assert.ThrowsAsync<IOException>(() =>
            NewRunner(vaultRoot: Path.Combine(blocker, "vault")).BeginAsync("clean", new[] { Target(MakeCache("cache")) }));

        Assert.StartsWith("The quarantine vault could not be opened: ", e.Message);
    }

    [Fact]
    public async Task SweepThenRollbackBringsEverythingBackAndLogsBothSteps()
    {
        var dir = MakeCache("cache");
        var before = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p), p => File.ReadAllBytes(p));
        var runner = NewRunner();

        var session = await runner.BeginAsync("clean", new[] { Target(dir) });
        var id = session!.Manifest.Id;
        var result = new CleanupService(_log, _settings, session.Quarantine, OperationAction.Quarantined)
            .Execute(new[] { Candidate(dir) }, id);
        session.Complete();

        Assert.Equal(1, result.RemovedCount);
        Assert.False(Directory.Exists(dir));
        Assert.Contains(_log.Read(), r => r.Action == OperationAction.Quarantined && r.RunId == id && r.Path == dir);
        Assert.Equal(id, runner.LastSweepId);

        var rollback = runner.Rollback(id);

        Assert.Equal(1, rollback.Restored);
        var after = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p), p => File.ReadAllBytes(p));
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (name, bytes) in before)
        {
            Assert.Equal(bytes, after[name]);
        }

        var restored = Assert.Single(_log.Read(), r => r.Action == OperationAction.Restored);
        Assert.Equal("rollback", restored.Command);
        Assert.Equal(id, restored.RunId);
        Assert.Equal(dir, restored.Path);
        Assert.Null(runner.LastSweepId);
    }

    [Fact]
    public async Task KeptEntryIsLoggedAsSkippedAndStaysTheLastSweep()
    {
        var dir = MakeCache("cache");
        var runner = NewRunner();
        var session = await runner.BeginAsync("clean", new[] { Target(dir) });
        session!.Quarantine(dir, 100);
        session.Complete();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "newer");

        runner.Rollback(session.Manifest.Id);

        var records = _log.Read();
        Assert.DoesNotContain(records, r => r.Action == OperationAction.Restored);
        var skipped = Assert.Single(records, r => r.Action == OperationAction.Skipped);
        Assert.Equal("rollback", skipped.Command);
        Assert.Contains("already existed", skipped.Detail);
        Assert.Equal(session.Manifest.Id, runner.LastSweepId);
    }

    [Fact]
    public void RollbackOfAnUnknownSweepLogsAFailure()
    {
        var result = NewRunner().Rollback("0000dead");

        Assert.Equal(0, result.Restored);
        var failed = Assert.Single(_log.Read());
        Assert.Equal(OperationAction.Failed, failed.Action);
        Assert.Equal("rollback", failed.Command);
        Assert.Equal("0000dead", failed.RunId);
    }

    [Fact]
    public async Task DeleteSweepRemovesTheFolderAndLogsIt()
    {
        var runner = NewRunner();
        var session = await runner.BeginAsync("clean", new[] { Target(MakeCache("cache")) });
        session!.Complete();
        var id = session.Manifest.Id;

        Assert.True(runner.DeleteSweep(id));
        Assert.False(runner.DeleteSweep(id));

        Assert.Empty(runner.History());
        var record = Assert.Single(_log.Read());
        Assert.Equal("vault", record.Command);
        Assert.Equal(OperationAction.Removed, record.Action);
        Assert.Equal(id, record.Path);
        Assert.Equal("Quarantined sweep deleted permanently", record.Detail);
    }

    [Fact]
    public async Task PurgeExpiredUsesTheRetentionSettingWithAMinimumOfOneDay()
    {
        _settings.VaultRetentionDays = 0;
        var runner = NewRunner();
        var session = await runner.BeginAsync("clean", new[] { Target(MakeCache("cache")) });
        session!.Complete();

        Assert.Equal(0, runner.PurgeExpired());
        Assert.Single(runner.History());
    }

    [Fact]
    public async Task VolumeSnapshotWithoutElevationStartsNothing()
    {
        var info = await VolumeSnapshot.CreateAsync(isElevated: false);

        Assert.Equal(new SnapshotInfo(false, false, VolumeSnapshot.NotElevatedMessage), info);
    }
}
