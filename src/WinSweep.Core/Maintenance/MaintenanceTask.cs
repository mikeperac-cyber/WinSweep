using WinSweep.Core.Logging;

namespace WinSweep.Core.Maintenance;

/// <summary>What a maintenance task is allowed to know about its surroundings.</summary>
public sealed class MaintenanceContext
{
    public MaintenanceContext(OperationLog log, string runId, Func<bool> isElevated)
    {
        Log = log;
        RunId = runId;
        IsElevated = isElevated;
    }

    public OperationLog Log { get; }
    public string RunId { get; }
    public Func<bool> IsElevated { get; }
}

/// <summary>
/// One bounded maintenance task. Every task states in plain words what it changes,
/// so the review screen can explain itself before anything runs.
/// </summary>
public sealed class MaintenanceTask
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>What the task does.</summary>
    public required string Description { get; init; }

    /// <summary>What changes on this machine when it runs.</summary>
    public required string Impact { get; init; }

    /// <summary>True when the task needs an administrator account.</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>True when the task only reads and reports.</summary>
    public bool IsCheck { get; init; }

    /// <summary>Hard time limit for the task.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>True when the task is supported on this version of Windows.</summary>
    public Func<bool>? IsSupported { get; init; }

    /// <summary>
    /// True when the task can run in a Microsoft Store build. Tasks that need an
    /// administrator account are not available there.
    /// </summary>
    public bool StoreAvailable { get; init; } = true;

    public required Func<MaintenanceContext, CancellationToken, Task<MaintenanceResult>> RunAsync { get; init; }
}
