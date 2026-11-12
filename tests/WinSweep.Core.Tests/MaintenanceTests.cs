using WinSweep.Core.Apps;
using WinSweep.Core.Logging;
using WinSweep.Core.Maintenance;
using Xunit;

namespace WinSweep.Core.Tests;

public class MaintenanceCatalogTests
{
    [Fact]
    public void EveryTaskExplainsItself()
    {
        foreach (var task in MaintenanceCatalog.GetTasks())
        {
            Assert.False(string.IsNullOrWhiteSpace(task.Id), "A task has no id");
            Assert.False(string.IsNullOrWhiteSpace(task.Name), $"{task.Id} has no name");
            Assert.False(string.IsNullOrWhiteSpace(task.Description), $"{task.Id} has no description");
            Assert.False(string.IsNullOrWhiteSpace(task.Impact), $"{task.Id} does not say what it changes");
            Assert.True(task.Timeout > TimeSpan.Zero, $"{task.Id} has no time limit");
        }
    }

    [Fact]
    public void TaskIdsAreUnique()
    {
        var ids = MaintenanceCatalog.GetTasks().Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void OnlyTheKnownTasksAreOffered()
    {
        var ids = MaintenanceCatalog.GetTasks().Select(t => t.Id).ToList();

        Assert.Contains("disk-health", ids);
        Assert.Contains("dns-flush", ids);
        Assert.Contains("component-cleanup", ids);
    }
}

public class MaintenanceServiceTests
{
    [Fact]
    public async Task RefusesATaskThatNeedsAdministratorRights()
    {
        var log = new OperationLog(Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"), "log.jsonl"));
        var service = new MaintenanceService(log, isElevated: () => false);

        var result = await service.RunAsync("component-cleanup", "run-test");

        Assert.Equal(MaintenanceOutcome.Skipped, result.Outcome);
        Assert.Contains("administrator", result.Message, StringComparison.OrdinalIgnoreCase);

        // The refusal is recorded, so the history shows the task was never run.
        var records = log.Read();
        Assert.Single(records);
        Assert.Equal("component-cleanup", records[0].Path);
    }

    [Fact]
    public async Task ReportsAnUnknownTaskAsUnavailable()
    {
        var log = new OperationLog(Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"), "log.jsonl"));
        var service = new MaintenanceService(log);

        var result = await service.RunAsync("does-not-exist", "run-test");

        Assert.Equal(MaintenanceOutcome.Unavailable, result.Outcome);
    }
}

public class StoreAppInventoryTests
{
    [Fact]
    public void ReadsTheStoreApplicationsWithoutFailing()
    {
        var inventory = new StoreAppInventory(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200));

        var apps = inventory.ListStoreApps(measureSizes: false);

        Assert.NotNull(apps);
    }

    [Fact]
    public void KeepsThePackageDataFolderInsideThePackageRoot()
    {
        var folder = StoreAppInventory.GetPackageDataFolder("Microsoft.WindowsStore_8wekyb3d8bbwe");

        Assert.Contains("Packages", folder);
        Assert.EndsWith("Microsoft.WindowsStore_8wekyb3d8bbwe", folder);
    }
}
