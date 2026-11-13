using WinSweep.Core.Cleanup;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Services;
using WinSweep.Core.Settings;
using Xunit;

namespace WinSweep.Core.Tests;

/// <summary>The guards must run before a custom remover (the quarantine vault) is ever called.</summary>
public class RemoverWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));

    public RemoverWiringTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static CleanupCandidate Candidate(string path, bool selected = true) => new()
    {
        Name = "fixture",
        Path = path,
        Category = "Temp",
        Description = "test fixture",
        SizeBytes = 1,
        SizeStatus = ScanStatus.Complete,
        IsSelected = selected
    };

    private static ProjectArtifact Artifact(ArtifactTree tree, string name, string path) => new()
    {
        ProjectName = Path.GetFileName(tree.Project),
        ProjectPath = tree.Project,
        Name = name,
        Path = path,
        Description = "Generated build output.",
        SizeBytes = 4096,
        SizeStatus = ScanStatus.Complete,
        LastActivityUtc = DateTime.UtcNow.AddDays(-10),
        IsSelected = true
    };

    [Fact]
    public void CleanupServiceNeverCallsTheRemoverForProtectedRejectedOrUnselectedPaths()
    {
        var protectedDir = Path.Combine(_root, "protected");
        var plainDir = Path.Combine(_root, "unselected");
        Directory.CreateDirectory(protectedDir);
        Directory.CreateDirectory(plainDir);
        var settings = new AppSettings { ProtectedPaths = { protectedDir } };
        var log = new OperationLog(Path.Combine(_root, "operations.jsonl"));
        var calls = new List<string>();
        var service = new CleanupService(log, settings, (path, _) => { calls.Add(path); return RemovalResult.Ok(1); },
            OperationAction.Quarantined);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var result = service.Execute(
            new[] { Candidate(protectedDir), Candidate(windows), Candidate(plainDir, selected: false) }, "run");

        Assert.Empty(calls);
        Assert.Equal(3, result.SkippedCount);
        Assert.Equal(0, result.RemovedCount);
        var records = log.Read();
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Equal(OperationAction.Skipped, r.Action));
    }

    [Fact]
    public void CleanupServiceLogsTheRemovedActionOnSuccess()
    {
        var dir = Path.Combine(_root, "cache");
        Directory.CreateDirectory(dir);
        var log = new OperationLog(Path.Combine(_root, "operations.jsonl"));
        var service = new CleanupService(log, new AppSettings(), (_, size) => RemovalResult.Ok(size), OperationAction.Quarantined);

        service.Execute(new[] { Candidate(dir) }, "run");

        Assert.Equal(OperationAction.Quarantined, Assert.Single(log.Read()).Action);
    }

    [Fact]
    public void ProjectArtifactServiceNeverCallsTheRemoverForAProtectedArtifact()
    {
        using var tree = new ArtifactTree();
        var path = tree.CreateArtifact("build", DateTime.UtcNow.AddDays(-10));
        var log = new OperationLog(Path.Combine(tree.Root, "operations.jsonl"));
        var calls = 0;
        var service = new ProjectArtifactService(log, new AppSettings { ProtectedPaths = { tree.Project } },
            (_, _) => { calls++; return RemovalResult.Ok(1); }, OperationAction.Quarantined);

        var result = service.Execute(new[] { Artifact(tree, "build", path) }, "run");

        Assert.Equal(0, calls);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(OperationAction.Skipped, Assert.Single(log.Read()).Action);
    }

    [Fact]
    public void ProjectArtifactServiceLogsQuarantinedWithTheVaultDetail()
    {
        using var tree = new ArtifactTree();
        var path = tree.CreateArtifact("build", DateTime.UtcNow.AddDays(-10));
        var log = new OperationLog(Path.Combine(tree.Root, "operations.jsonl"));
        var service = new ProjectArtifactService(log, new AppSettings(), (_, size) => RemovalResult.Ok(size), OperationAction.Quarantined);

        var result = service.Execute(new[] { Artifact(tree, "build", path) }, "run");

        Assert.Equal(1, result.RecycledCount);
        var record = Assert.Single(log.Read());
        Assert.Equal(OperationAction.Quarantined, record.Action);
        Assert.Equal("Moved to the quarantine vault", record.Detail);
    }

    [Fact]
    public void ProjectArtifactServiceFailureTextNamesTheVault()
    {
        using var tree = new ArtifactTree();
        var path = tree.CreateArtifact("build", DateTime.UtcNow.AddDays(-10));
        var log = new OperationLog(Path.Combine(tree.Root, "operations.jsonl"));
        var service = new ProjectArtifactService(log, new AppSettings(), (_, _) => new RemovalResult(false, 0, null), OperationAction.Quarantined);

        service.Execute(new[] { Artifact(tree, "build", path) }, "run");

        Assert.Equal("The folder could not be moved to the quarantine vault", Assert.Single(log.Read()).Detail);
    }
}
