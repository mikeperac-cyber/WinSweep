using WinSweep.Core.Apps;
using WinSweep.Core.Cleanup;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Settings;
using Xunit;

namespace WinSweep.Core.Tests;

public class RemovalPathTests
{
    [Fact]
    public void RemovesASelectedFolderAndRecordsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "cache");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "blob.bin"), "x");

        var logPath = Path.Combine(root, "operations.jsonl");
        var log = new OperationLog(logPath);

        // The Recycle Bin is switched off so the test fixture never lands in the
        // user's Recycle Bin.
        var settings = new AppSettings { UseRecycleBin = false };
        var service = new CleanupService(log, settings);

        var candidate = new CleanupCandidate
        {
            Name = "cache",
            Path = target,
            Category = "System",
            Description = "test fixture",
            SizeBytes = 1,
            SizeStatus = ScanStatus.Complete,
            IsSelected = true
        };

        var result = service.Execute(new[] { candidate }, "run-test");

        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.False(Directory.Exists(target));

        var records = log.Read();
        var record = Assert.Single(records);
        Assert.Equal(OperationAction.Removed, record.Action);
        Assert.Equal("clean", record.Command);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void KeepsAFolderThatTheUserProtected()
    {
        var root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "keepme");
        Directory.CreateDirectory(target);

        var log = new OperationLog(Path.Combine(root, "operations.jsonl"));
        var settings = new AppSettings
        {
            UseRecycleBin = false,
            ProtectedPaths = new List<string> { target }
        };
        var service = new CleanupService(log, settings);

        var candidate = new CleanupCandidate
        {
            Name = "keepme",
            Path = target,
            Category = "System",
            Description = "test fixture",
            IsSelected = true
        };

        var result = service.Execute(new[] { candidate }, "run-test");

        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(Directory.Exists(target));

        Directory.Delete(root, recursive: true);
    }
}

public class UninstallCommandTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\"", "C:\\Program Files\\App\\unins000.exe", "")]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "C:\\Program Files\\App\\unins000.exe", "/SILENT")]
    [InlineData("MsiExec.exe /X{1234-5678}", "MsiExec.exe", "/X{1234-5678}")]
    [InlineData("C:\\Windows\\System32\\appwiz.cpl", "C:\\Windows\\System32\\appwiz.cpl", "")]
    public void SplitsTheRegisteredUninstallCommand(string command, string expectedExecutable, string expectedArguments)
    {
        var (executable, arguments) = UninstallService.SplitCommand(command);

        Assert.Equal(expectedExecutable, executable);
        Assert.Equal(expectedArguments, arguments);
    }
}
