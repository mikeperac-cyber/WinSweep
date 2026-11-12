namespace WinSweep.Core.Models;

public sealed record CpuSnapshot(double TotalUsagePercent, int LogicalProcessors, IReadOnlyList<double> PerCoreUsagePercent)
{
    public static CpuSnapshot Empty { get; } = new(0, 0, Array.Empty<double>());
}

public sealed record MemorySnapshot(long TotalBytes, long AvailableBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);
    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
    public static MemorySnapshot Empty { get; } = new(0, 0);
}

public sealed record DriveSnapshot(string Name, string VolumeLabel, long TotalBytes, long FreeBytes, string DriveType)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
    public string UsedDisplay => $"{Core.Models.ByteSizeFormatter.Format(UsedBytes)} of {Core.Models.ByteSizeFormatter.Format(TotalBytes)}";
    public string FreeDisplay => $"{Core.Models.ByteSizeFormatter.Format(FreeBytes)} free";
}

public sealed record NetworkSnapshot(string InterfaceName, string Description, long BytesReceived, long BytesSent, string Status)
{
    public double MegabytesReceivedPerSecond => BytesReceived / 1024.0 / 1024.0;
    public double MegabytesSentPerSecond => BytesSent / 1024.0 / 1024.0;
}

public sealed record ProcessSnapshot(int ProcessId, string Name, double CpuPercent, long WorkingSetBytes)
{
    public string MemoryDisplay => ByteSizeFormatter.Format(WorkingSetBytes);
}

public sealed record BatterySnapshot(string Name, int ChargePercent, string Status, bool IsPresent);

/// <summary>A read-only snapshot of the machine's health.</summary>
public sealed class SystemSnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public string HostName { get; init; } = Environment.MachineName;
    public string OperatingSystem { get; init; } = string.Empty;
    public TimeSpan Uptime { get; init; }
    public CpuSnapshot Cpu { get; init; } = CpuSnapshot.Empty;
    public MemorySnapshot Memory { get; init; } = MemorySnapshot.Empty;
    public IReadOnlyList<DriveSnapshot> Drives { get; init; } = Array.Empty<DriveSnapshot>();
    public IReadOnlyList<NetworkSnapshot> Networks { get; init; } = Array.Empty<NetworkSnapshot>();
    public IReadOnlyList<ProcessSnapshot> TopProcesses { get; init; } = Array.Empty<ProcessSnapshot>();
    public IReadOnlyList<BatterySnapshot> Batteries { get; init; } = Array.Empty<BatterySnapshot>();

    /// <summary>0-100. Combines CPU pressure, memory pressure and free space on the system drive.</summary>
    public int HealthScore { get; init; }

    public string HealthSummary => HealthScore switch
    {
        >= 85 => "Healthy",
        >= 70 => "Fair",
        >= 50 => "Under pressure",
        _ => "Stressed"
    };
}
