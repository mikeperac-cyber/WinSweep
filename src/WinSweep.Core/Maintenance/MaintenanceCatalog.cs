using System.Text.Json;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Services;

namespace WinSweep.Core.Maintenance;

/// <summary>
/// The bounded set of maintenance tasks this app offers. Each one is a single, known
/// action with a stated effect. There is no registry cleaner and no "boost" mode.
/// </summary>
public static class MaintenanceCatalog
{
    public static IReadOnlyList<MaintenanceTask> GetTasks() => Tasks;

    private static readonly MaintenanceTask[] Tasks =
    {
        new()
        {
            Id = "disk-health",
            Name = "Disk health check",
            Description = "Reads the health status that the storage driver reports for each physical disk.",
            Impact = "Nothing changes. This only reads the drive health.",
            IsCheck = true,
            Timeout = TimeSpan.FromMinutes(1),
            RunAsync = RunDiskHealthAsync
        },
        new()
        {
            Id = "dns-flush",
            Name = "Flush the DNS cache",
            Description = "Clears the resolver cache that Windows keeps in memory.",
            Impact = "The next web address lookup goes to the server again. Nothing is deleted from disk.",
            Timeout = TimeSpan.FromMinutes(1),
            RunAsync = async (context, cancellationToken) =>
                await CommandRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromMinutes(1), requiresElevation: false, cancellationToken)
        },
        new()
        {
            Id = "icon-cache",
            Name = "Rebuild the icon cache",
            Description = "Removes the cached copies of icons that Windows keeps for fast display.",
            Impact = "Icons are drawn again on the next refresh. Explorer rebuilds them by itself.",
            Timeout = TimeSpan.FromMinutes(2),
            RunAsync = RunIconCacheRebuildAsync
        },
        new()
        {
            Id = "delivery-optimization",
            Name = "Clear the Delivery Optimization cache",
            Description = "Clears the copies of update and application files that Windows keeps to share updates with other machines.",
            Impact = "Updates may be downloaded again later. Nothing that is pending an update is touched.",
            RequiresElevation = true,
            Timeout = TimeSpan.FromMinutes(5),
            IsSupported = () => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 1709),
            RunAsync = async (context, cancellationToken) =>
                await CommandRunner.RunAsync(
                    "powershell.exe",
                    "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force\"",
                    TimeSpan.FromMinutes(5),
                    requiresElevation: true,
                    cancellationToken)
        },
        new()
        {
            Id = "component-cleanup",
            Name = "Clean up superseded system components",
            Description = "Runs the Windows component cleanup, which removes the older versions of system files that updates have replaced.",
            Impact = "The old copies are removed. Undoing an update that Windows no longer supports becomes impossible.",
            RequiresElevation = true,
            Timeout = TimeSpan.FromMinutes(30),
            RunAsync = async (context, cancellationToken) =>
                await CommandRunner.RunAsync(
                    "Dism.exe",
                    "/Online /Cleanup-Image /StartComponentCleanup",
                    TimeSpan.FromMinutes(30),
                    requiresElevation: true,
                    cancellationToken)
        },
        new()
        {
            Id = "trim",
            Name = "Optimize the system drive",
            Description = "Runs the storage trim pass so solid state drives keep their write performance.",
            Impact = "The drive is rewritten in place. It is safe, and it can take a few minutes on a large drive.",
            RequiresElevation = true,
            Timeout = TimeSpan.FromMinutes(30),
            RunAsync = async (context, cancellationToken) =>
                await CommandRunner.RunAsync(
                    "defrag.exe",
                    $"{SystemDrive()} /O",
                    TimeSpan.FromMinutes(30),
                    requiresElevation: true,
                    cancellationToken)
        },
        new()
        {
            Id = "system-file-verify",
            Name = "Verify the system files",
            Description = "Checks the Windows system files and reports whether any of them are damaged.",
            Impact = "Nothing changes. This only verifies, it does not repair.",
            IsCheck = true,
            RequiresElevation = true,
            Timeout = TimeSpan.FromMinutes(30),
            RunAsync = async (context, cancellationToken) =>
                await CommandRunner.RunAsync(
                    "sfc.exe",
                    "/verifyonly",
                    TimeSpan.FromMinutes(30),
                    requiresElevation: true,
                    cancellationToken)
        }
    };

    private static string SystemDrive()
    {
        var drive = Environment.GetEnvironmentVariable("SystemDrive");
        return string.IsNullOrWhiteSpace(drive) ? "C:" : drive;
    }

    private static async Task<MaintenanceResult> RunDiskHealthAsync(MaintenanceContext context, CancellationToken cancellationToken)
    {
        var result = await CommandRunner.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Get-PhysicalDisk | Select-Object FriendlyName,HealthStatus,MediaType | ConvertTo-Json -Compress\"",
            TimeSpan.FromMinutes(1),
            requiresElevation: false,
            cancellationToken);

        if (result.Outcome != MaintenanceOutcome.Applied)
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var disks = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { document.RootElement };

            if (disks.Count == 0)
            {
                return MaintenanceResult.Unchanged("Windows did not report any physical disk.");
            }

            var lines = disks.Select(d =>
            {
                var name = d.TryGetProperty("FriendlyName", out var n) ? n.GetString() : "Disk";
                var health = d.TryGetProperty("HealthStatus", out var h) ? h.GetString() : "Unknown";
                var media = d.TryGetProperty("MediaType", out var m) ? m.GetString() : "Unknown";
                return $"{name} ({media}): {health}";
            });

            var unhealthy = disks.Count(d =>
                d.TryGetProperty("HealthStatus", out var h) &&
                !string.Equals(h.GetString(), "Healthy", StringComparison.OrdinalIgnoreCase));

            var message = unhealthy == 0
                ? $"{disks.Count} drive(s) report a healthy status."
                : $"{unhealthy} drive(s) do not report a healthy status.";

            return unhealthy == 0
                ? MaintenanceResult.Applied(message, string.Join(Environment.NewLine, lines))
                : MaintenanceResult.Failed(message, string.Join(Environment.NewLine, lines));
        }
        catch (JsonException)
        {
            return MaintenanceResult.Unchanged("Windows did not report a drive health status.", result.Output);
        }
    }

    private static Task<MaintenanceResult> RunIconCacheRebuildAsync(MaintenanceContext context, CancellationToken cancellationToken)
    {
        var cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "Windows",
            "IconCache");

        if (!Directory.Exists(cacheFolder))
        {
            return Task.FromResult(MaintenanceResult.Unchanged("There is no icon cache to rebuild."));
        }

        var removed = 0;
        long bytes = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(cacheFolder, "*.db"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var size = new FileInfo(file).Length;
                var result = RecycleBin.Send(file, size);
                if (result.Succeeded)
                {
                    removed++;
                    bytes += result.BytesReclaimed;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(MaintenanceResult.Failed($"The icon cache could not be rebuilt: {e.Message}"));
        }

        return Task.FromResult(removed == 0
            ? MaintenanceResult.Unchanged("The icon cache was already empty.")
            : MaintenanceResult.Applied(
                $"Rebuilt the icon cache: {removed} file(s), {ByteSizeFormatter.Format(bytes)}.",
                string.Empty));
    }
}
