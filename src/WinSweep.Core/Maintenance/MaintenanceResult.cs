namespace WinSweep.Core.Maintenance;

/// <summary>What happened when a maintenance task ran.</summary>
public enum MaintenanceOutcome
{
    /// <summary>The task did its work.</summary>
    Applied,

    /// <summary>The task ran and there was nothing to do.</summary>
    Unchanged,

    /// <summary>The task was not run, for example because something was in use.</summary>
    Skipped,

    /// <summary>The task is not supported on this machine or this version of Windows.</summary>
    Unavailable,

    /// <summary>The task failed.</summary>
    Failed
}

/// <summary>The result of one maintenance task.</summary>
public sealed record MaintenanceResult(
    MaintenanceOutcome Outcome,
    string Message,
    string Output = "",
    int? ExitCode = null)
{
    public static MaintenanceResult Applied(string message, string output = "") =>
        new(MaintenanceOutcome.Applied, message, output);

    public static MaintenanceResult Unchanged(string message, string output = "") =>
        new(MaintenanceOutcome.Unchanged, message, output);

    public static MaintenanceResult Skipped(string message) =>
        new(MaintenanceOutcome.Skipped, message);

    public static MaintenanceResult Unavailable(string message) =>
        new(MaintenanceOutcome.Unavailable, message);

    public static MaintenanceResult Failed(string message, string output = "", int? exitCode = null) =>
        new(MaintenanceOutcome.Failed, message, output, exitCode);
}
