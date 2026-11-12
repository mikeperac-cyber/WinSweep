using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using WinSweep.Core.Models;

namespace WinSweep.Core.Status;

/// <summary>
/// Collects a read-only snapshot of the machine. Nothing here writes to the system,
/// and a collector that fails never stops the other collectors.
/// </summary>
public sealed class SystemStatusCollector
{
    private readonly TimeSpan _sampleWindow;

    public SystemStatusCollector(TimeSpan? sampleWindow = null)
    {
        _sampleWindow = sampleWindow ?? TimeSpan.FromMilliseconds(400);
    }

    public SystemSnapshot Collect()
    {
        var cpu = CollectCpu();
        var memory = CollectMemory();
        var drives = CollectDrives();
        var networks = CollectNetworks();
        var processes = CollectTopProcesses();
        var batteries = CollectBatteries();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        return new SystemSnapshot
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            Uptime = uptime,
            Cpu = cpu,
            Memory = memory,
            Drives = drives,
            Networks = networks,
            TopProcesses = processes,
            Batteries = batteries,
            HealthScore = ComputeHealthScore(cpu, memory, drives)
        };
    }

    private static CpuSnapshot CollectCpu()
    {
        var processors = Environment.ProcessorCount;
        try
        {
            if (!NativeMethods.GetSystemTimes(out var idle1, out var kernel1, out var user1))
            {
                return CpuSnapshot.Empty;
            }

            Thread.Sleep(300);

            if (!NativeMethods.GetSystemTimes(out var idle2, out var kernel2, out var user2))
            {
                return CpuSnapshot.Empty;
            }

            var idle = NativeMethods.ToMilliseconds(idle2) - NativeMethods.ToMilliseconds(idle1);
            var kernel = NativeMethods.ToMilliseconds(kernel2) - NativeMethods.ToMilliseconds(kernel1);
            var user = NativeMethods.ToMilliseconds(user2) - NativeMethods.ToMilliseconds(user1);
            var total = kernel + user;

            if (total <= 0)
            {
                return CpuSnapshot.Empty;
            }

            var usage = Math.Clamp((total - idle) / total * 100.0, 0, 100);
            return new CpuSnapshot(Math.Round(usage, 1), processors, Array.Empty<double>());
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return CpuSnapshot.Empty;
        }
    }

    private static MemorySnapshot CollectMemory()
    {
        try
        {
            var status = new NativeMethods.MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                return MemorySnapshot.Empty;
            }

            return new MemorySnapshot((long)status.ullTotalPhys, (long)status.ullAvailPhys);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return MemorySnapshot.Empty;
        }
    }

    private static IReadOnlyList<DriveSnapshot> CollectDrives()
    {
        var drives = new List<DriveSnapshot>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                drives.Add(new DriveSnapshot(
                    drive.Name,
                    drive.VolumeLabel,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    drive.DriveType.ToString()));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Return what was collected so far.
        }

        return drives;
    }

    private static IReadOnlyList<NetworkSnapshot> CollectNetworks()
    {
        var networks = new List<NetworkSnapshot>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var statistics = adapter.GetIPStatistics();
                networks.Add(new NetworkSnapshot(
                    adapter.Name,
                    adapter.Description,
                    statistics.BytesReceived,
                    statistics.BytesSent,
                    adapter.OperationalStatus.ToString()));
            }
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
            // Return what was collected so far.
        }

        return networks;
    }

    private static IReadOnlyList<ProcessSnapshot> CollectTopProcesses()
    {
        var samples = new List<(int Pid, string Name, double Cpu, long Memory)>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                samples.Add((process.Id, process.ProcessName, 0, process.WorkingSet64));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // A process that exits while it is being inspected is simply skipped.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (samples.Count == 0)
        {
            return Array.Empty<ProcessSnapshot>();
        }

        Thread.Sleep(400);

        var results = new List<ProcessSnapshot>();
        foreach (var sample in samples)
        {
            try
            {
                using var process = Process.GetProcessById(sample.Pid);
                var elapsed = (DateTime.Now - process.StartTime).TotalSeconds;
                var cpu = elapsed > 0 ? (process.TotalProcessorTime.TotalSeconds / elapsed) * 100.0 / Environment.ProcessorCount : 0;
                results.Add(new ProcessSnapshot(sample.Pid, sample.Name, Math.Round(cpu, 1), process.WorkingSet64));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process ended during the sample window.
            }
        }

        return results
            .OrderByDescending(p => p.CpuPercent)
            .ThenByDescending(p => p.WorkingSetBytes)
            .Take(12)
            .ToList();
    }

    private static IReadOnlyList<BatterySnapshot> CollectBatteries()
    {
        try
        {
            if (!NativeMethods.GetSystemPowerStatus(out var power))
            {
                return Array.Empty<BatterySnapshot>();
            }

            var hasBattery = (power.BatteryFlag & 0x80) == 0 && power.BatteryFlag != 255;
            if (!hasBattery)
            {
                return Array.Empty<BatterySnapshot>();
            }

            var percentage = power.BatteryLifePercent == 255 ? 0 : power.BatteryLifePercent;
            var status = power.ACLineStatus switch
            {
                1 => "Charging",
                0 => "On battery",
                _ => "Unknown"
            };

            return new[] { new BatterySnapshot("System battery", percentage, status, true) };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return Array.Empty<BatterySnapshot>();
        }
    }

    private static int ComputeHealthScore(CpuSnapshot cpu, MemorySnapshot memory, IReadOnlyList<DriveSnapshot> drives)
    {
        var score = 100.0;

        score -= Math.Clamp(cpu.TotalUsagePercent, 0, 100) * 0.25;
        score -= Math.Clamp(memory.UsedPercent, 0, 100) * 0.25;

        var systemDrive = drives.FirstOrDefault(d =>
            string.Equals(d.Name, Environment.GetEnvironmentVariable("SystemDrive") + "\\", StringComparison.OrdinalIgnoreCase));

        if (systemDrive is not null && systemDrive.TotalBytes > 0)
        {
            var freePercent = systemDrive.FreeBytes * 100.0 / systemDrive.TotalBytes;
            if (freePercent < 10)
            {
                score -= 30;
            }
            else if (freePercent < 20)
            {
                score -= 15;
            }
        }

        return (int)Math.Clamp(Math.Round(score), 0, 100);
    }
}
