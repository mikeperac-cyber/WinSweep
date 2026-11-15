using WinSweep.Core.Apps;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Settings;
using Xunit;

namespace WinSweep.Core.Tests;

/// <summary>The leftover review uses the same Core guards as the other removal paths.</summary>
public class UninstallServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));
    private readonly OperationLog _log;

    public UninstallServiceTests()
    {
        Directory.CreateDirectory(_root);
        _log = new OperationLog(Path.Combine(_root, "operations.jsonl"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void RemoveLeftoverKeepsAProtectedFolderAndLogsItAsSkipped()
    {
        var folder = Path.Combine(_root, "leftover");
        Directory.CreateDirectory(folder);
        var settings = new AppSettings { ProtectedPaths = { folder } };

        var result = new UninstallService(_log).RemoveLeftover(folder, 100, settings, "run");

        Assert.False(result.Succeeded);
        Assert.Equal(UninstallService.ProtectedMessage, result.FailureReason);
        Assert.True(Directory.Exists(folder));
        var record = Assert.Single(_log.Read());
        Assert.Equal(OperationAction.Skipped, record.Action);
        Assert.Equal(UninstallService.ProtectedMessage, record.Detail);
    }

    [Fact]
    public void RemoveLeftoverRefusesASystemLocationFromTheSafetyGuard()
    {
        var windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");

        var result = new UninstallService(_log).RemoveLeftover(windows, 0, new AppSettings(), "run");

        Assert.False(result.Succeeded);
        Assert.NotEqual(UninstallService.ProtectedMessage, result.FailureReason);
        var record = Assert.Single(_log.Read());
        Assert.Equal(OperationAction.Skipped, record.Action);
    }
}
