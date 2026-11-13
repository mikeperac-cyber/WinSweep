namespace WinSweep.Core.Models;

/// <summary>How destructive an operation was.</summary>
public enum OperationAction
{
    Removed,
    Recycled,
    Uninstalled,
    Completed,
    Skipped,
    Failed,
    Quarantined,
    Restored
}

/// <summary>One line of the operation history.</summary>
public sealed record OperationRecord
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Command { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public OperationAction Action { get; init; }
    public string Path { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
}
