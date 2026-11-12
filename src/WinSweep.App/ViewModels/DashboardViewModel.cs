using System.Collections.ObjectModel;
using WinSweep.Core.Models;
using WinSweep.Core.Status;

namespace WinSweep.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly SystemStatusCollector _collector = new();
    private SystemSnapshot? _snapshot;
    private bool _isBusy;

    public DashboardViewModel()
    {
        Drives = new ObservableCollection<DriveSnapshot>();
        TopProcesses = new ObservableCollection<ProcessSnapshot>();
    }

    public ObservableCollection<DriveSnapshot> Drives { get; }
    public ObservableCollection<ProcessSnapshot> TopProcesses { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public SystemSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (SetProperty(ref _snapshot, value))
            {
                OnPropertyChanged(nameof(HealthScoreText));
                OnPropertyChanged(nameof(HealthSummary));
                OnPropertyChanged(nameof(CpuText));
                OnPropertyChanged(nameof(MemoryText));
                OnPropertyChanged(nameof(UptimeText));
                OnPropertyChanged(nameof(HostText));
                OnPropertyChanged(nameof(BatteryText));
                OnPropertyChanged(nameof(CpuValueText));
                OnPropertyChanged(nameof(CpuDetailText));
                OnPropertyChanged(nameof(MemoryValueText));
                OnPropertyChanged(nameof(MemoryDetailText));
                OnPropertyChanged(nameof(SystemDriveText));
                OnPropertyChanged(nameof(SystemDriveFreeText));
                OnPropertyChanged(nameof(SystemDriveUsedPercent));
                OnPropertyChanged(nameof(BatteryValueText));
                OnPropertyChanged(nameof(BatteryDetailText));
                OnPropertyChanged(nameof(NetworkSummary));
                OnPropertyChanged(nameof(NetworkDetails));
            }
        }
    }

    public string HealthScoreText => Snapshot is null ? "--" : Snapshot.HealthScore.ToString();

    public string HealthSummary => Snapshot?.HealthSummary ?? "Collecting";

    public string HostText => Snapshot is null
        ? string.Empty
        : $"{Snapshot.HostName} · {Snapshot.OperatingSystem}";

    public string CpuText => Snapshot is null || Snapshot.Cpu.LogicalProcessors == 0
        ? "CPU --"
        : $"CPU {Snapshot.Cpu.TotalUsagePercent:0.#}% of {Snapshot.Cpu.LogicalProcessors} logical processors";

    public string MemoryText => Snapshot is null || Snapshot.Memory.TotalBytes == 0
        ? "Memory --"
        : $"Memory {Snapshot.Memory.UsedPercent:0.#}% of {Core.Models.ByteSizeFormatter.Format(Snapshot.Memory.TotalBytes)}";

    public string UptimeText => Snapshot is null
        ? string.Empty
        : $"Up {Snapshot.Uptime.Days}d {Snapshot.Uptime.Hours}h {Snapshot.Uptime.Minutes}m";

    public string BatteryText => Snapshot is null || Snapshot.Batteries.Count == 0
        ? string.Empty
        : $"Battery {Snapshot.Batteries[0].ChargePercent}% · {Snapshot.Batteries[0].Status}";

    public string CpuValueText => Snapshot is null || Snapshot.Cpu.LogicalProcessors == 0
        ? "--"
        : $"{Snapshot.Cpu.TotalUsagePercent:0}";

    public string CpuDetailText => Snapshot is null || Snapshot.Cpu.LogicalProcessors == 0
        ? "CPU reading unavailable"
        : $"% across {Snapshot.Cpu.LogicalProcessors} logical processors";

    public string MemoryValueText => Snapshot is null || Snapshot.Memory.TotalBytes == 0
        ? "--"
        : $"{Snapshot.Memory.UsedPercent:0}";

    public string MemoryDetailText => Snapshot is null || Snapshot.Memory.TotalBytes == 0
        ? "Memory reading unavailable"
        : $"{ByteSizeFormatter.Format(Snapshot.Memory.UsedBytes)} used of {ByteSizeFormatter.Format(Snapshot.Memory.TotalBytes)}";

    private DriveSnapshot? SystemDrive => Snapshot?.Drives.FirstOrDefault(d =>
        string.Equals(d.Name.TrimEnd('\\'), Environment.GetEnvironmentVariable("SystemDrive"), StringComparison.OrdinalIgnoreCase));

    public string SystemDriveText => SystemDrive is null
        ? "--"
        : ByteSizeFormatter.Format(SystemDrive.FreeBytes);

    public string SystemDriveFreeText => SystemDrive is null
        ? "System drive unavailable"
        : $"{SystemDrive.Name} free";

    public double SystemDriveUsedPercent => SystemDrive?.UsedPercent ?? 0;

    public string BatteryValueText => Snapshot is null || Snapshot.Batteries.Count == 0
        ? "—"
        : $"{Snapshot.Batteries[0].ChargePercent}%";

    public string BatteryDetailText => Snapshot is null || Snapshot.Batteries.Count == 0
        ? "No battery detected"
        : Snapshot.Batteries[0].Status;

    public string NetworkSummary => Snapshot is null
        ? "Collecting interface status"
        : Snapshot.Networks.Count == 0
            ? "No active network interface"
            : $"{Snapshot.Networks.Count} active network interface(s)";

    public string NetworkDetails => Snapshot is null || Snapshot.Networks.Count == 0
        ? string.Empty
        : string.Join(" · ", Snapshot.Networks.Take(2).Select(n => n.Description));

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // The collector samples the machine, so it runs off the UI thread.
            var snapshot = await Task.Run(_collector.Collect);

            Snapshot = snapshot;

            Drives.Clear();
            foreach (var drive in snapshot.Drives)
            {
                Drives.Add(drive);
            }

            TopProcesses.Clear();
            foreach (var process in snapshot.TopProcesses)
            {
                TopProcesses.Add(process);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
