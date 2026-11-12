using WinSweep.Core.Cleanup;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Settings;
using Xunit;

namespace WinSweep.Core.Tests;

public class OperationLogTests
{
    [Fact]
    public void WritesAndReadsBackEveryAction()
    {
        var path = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"), "operations.jsonl");
        var log = new OperationLog(path);

        log.Append(new OperationRecord
        {
            Command = "clean",
            RunId = "run-1",
            Action = OperationAction.Recycled,
            Path = @"C:\Users\Me\AppData\Local\Temp\cache",
            Detail = "Temporary files",
            SizeBytes = 1_024
        });

        var records = log.Read();

        var record = Assert.Single(records);
        Assert.Equal("clean", record.Command);
        Assert.Equal(OperationAction.Recycled, record.Action);
        Assert.Equal(1_024, record.SizeBytes);
    }

    [Fact]
    public void KeepsTheEarlierHistoryWhenOneLineIsDamaged()
    {
        var path = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"), "operations.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"Command\":\"clean\",\"Action\":\"Removed\"}\nthis is not json\n");

        var log = new OperationLog(path);
        var records = log.Read();

        Assert.Single(records);
        Assert.Equal("clean", records[0].Command);
    }

    [Fact]
    public void ReadsBackTheNewestActionFirst()
    {
        var path = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"), "operations.jsonl");
        var log = new OperationLog(path);

        log.Append(new OperationRecord { Command = "clean", Action = OperationAction.Removed, Path = "a" });
        log.Append(new OperationRecord { Command = "clean", Action = OperationAction.Removed, Path = "b" });

        var records = log.Read();

        Assert.Equal("b", records[0].Path);
        Assert.Equal("a", records[1].Path);
    }
}

public class CleanupServiceTests
{
    [Fact]
    public void SkipsCandidatesThatWereNotSelected()
    {
        var log = new OperationLog(Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"), "log.jsonl"));
        var service = new CleanupService(log, new AppSettings());

        var candidate = new CleanupCandidate
        {
            Name = "cache",
            Path = Path.Combine(Path.GetTempPath(), "winsweep-tests", "not-removed"),
            Category = "System",
            Description = "test",
            IsSelected = false
        };

        var result = service.Execute(new[] { candidate }, "run-1");

        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(Directory.GetParent(candidate.Path)!.Exists);
    }

    [Fact]
    public void RefusesToRemoveAProtectedLocationEvenWhenSelected()
    {
        var log = new OperationLog(Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"), "log.jsonl"));
        var service = new CleanupService(log, new AppSettings());

        var candidate = new CleanupCandidate
        {
            Name = "windows",
            Path = @"C:\Windows\Temp",
            Category = "System",
            Description = "test",
            IsSelected = true
        };

        var result = service.Execute(new[] { candidate }, "run-1");

        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.BytesReclaimed);
    }
}

public class PathTemplateExpanderTests
{
    [Fact]
    public void ExpandsEnvironmentVariables()
    {
        var expanded = PathTemplateExpander.Expand("%LOCALAPPDATA%\\Temp");

        Assert.Single(expanded);
        Assert.EndsWith("Temp", expanded[0]);
        Assert.DoesNotContain("%", expanded[0]);
    }

    [Fact]
    public void ExpandsOneWildcardLevel()
    {
        var root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Profile1", "Cache"));
        Directory.CreateDirectory(Path.Combine(root, "Profile2", "Cache"));
        Directory.CreateDirectory(Path.Combine(root, "Profile1", "Other"));

        var expanded = PathTemplateExpander.Expand(Path.Combine(root, "*", "Cache"));

        Assert.Equal(2, expanded.Count);
        Assert.Contains(expanded, p => p.EndsWith(Path.Combine("Profile1", "Cache")));
        Assert.Contains(expanded, p => p.EndsWith(Path.Combine("Profile2", "Cache")));
    }

    [Fact]
    public void ReturnsNothingWhenTheBaseFolderIsMissing()
    {
        var expanded = PathTemplateExpander.Expand(
            Path.Combine(Path.GetTempPath(), "winsweep-tests", "missing-" + Guid.NewGuid().ToString("N"), "*", "Cache"));

        Assert.Empty(expanded);
    }
}
