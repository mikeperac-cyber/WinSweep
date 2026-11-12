namespace WinSweep.Core.Models;

/// <summary>One entry in the disk tree.</summary>
public sealed class DiskEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public bool IsDirectory { get; init; }
    public ScanStatus SizeStatus { get; init; } = ScanStatus.Complete;
    public DateTime? LastWriteTimeUtc { get; init; }

    /// <summary>True for locations known to hold a lot of disposable data.</summary>
    public bool IsInsight { get; init; }

    public string SizeDisplay => SizeStatus == ScanStatus.Unavailable
        ? "Unknown"
        : ByteSizeFormatter.Format(SizeBytes);
}

/// <summary>Result of a disk scan.</summary>
public sealed class DiskScanResult
{
    public required string Path { get; init; }
    public ScanStatus Status { get; init; } = ScanStatus.Complete;
    public long TotalSizeBytes { get; init; }
    public long TotalFileCount { get; init; }
    public IReadOnlyList<DiskEntry> Entries { get; init; } = Array.Empty<DiskEntry>();
    public IReadOnlyList<DiskEntry> LargeFiles { get; init; } = Array.Empty<DiskEntry>();
    public IReadOnlyList<string> UnreadablePaths { get; init; } = Array.Empty<string>();

    /// <summary>True when the scan ran out of its time budget before finishing.</summary>
    public bool TimedOut { get; init; }

    public TimeSpan Duration { get; init; }
}
