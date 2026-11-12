using WinSweep.Core.Logging;
using WinSweep.Core.Models;

namespace WinSweep.Core.Maintenance;

/// <summary>
/// Runs maintenance tasks and records what happened. The elevation gate lives here:
/// a task that needs an administrator account never starts half-way.
/// </summary>
public sealed class MaintenanceService
{
    private readonly OperationLog _log;
    private readonly Func<bool> _isElevated;

    public MaintenanceService(OperationLog log, Func<bool>? isElevated = null)
    {
        _log = log;
        _isElevated = isElevated ?? (() => false);
    }

    /// <summary>Runs one task by its id and returns the outcome.</summary>
    public async Task<MaintenanceResult> RunAsync(
        string taskId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        var task = MaintenanceCatalog.GetTasks().FirstOrDefault(t => t.Id == taskId);
        if (task is null)
        {
            return MaintenanceResult.Unavailable("That maintenance task does not exist.");
        }

        if (task.IsSupported is not null && !task.IsSupported())
        {
            var unsupported = MaintenanceResult.Unavailable("This task is not supported on this version of Windows.");
            Log(task, runId, unsupported);
            return unsupported;
        }

        if (task.RequiresElevation && !_isElevated())
        {
            var refused = MaintenanceResult.Skipped("This task needs an administrator account.");
            Log(task, runId, refused);
            return refused;
        }

        var context = new MaintenanceContext(_log, runId, _isElevated);

        MaintenanceResult result;
        try
        {
            result = await task.RunAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = MaintenanceResult.Skipped("The task was cancelled.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            result = MaintenanceResult.Failed(e.Message);
        }

        Log(task, runId, result);
        return result;
    }

    private void Log(MaintenanceTask task, string runId, MaintenanceResult result)
    {
        _log.Append(new OperationRecord
        {
            Command = "maintenance",
            RunId = runId,
            Action = result.Outcome switch
            {
                MaintenanceOutcome.Applied => OperationAction.Completed,
                MaintenanceOutcome.Failed => OperationAction.Failed,
                _ => OperationAction.Skipped
            },
            Path = task.Id,
            Detail = $"{task.Name}: {result.Message}"
        });
    }
}
