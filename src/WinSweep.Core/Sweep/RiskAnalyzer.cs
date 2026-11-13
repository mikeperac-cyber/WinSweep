using System.ComponentModel;
using System.Diagnostics;
using WinSweep.Core.Apps;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;

namespace WinSweep.Core.Sweep;

/// <summary>Rates how safe a path is to sweep, from the installed apps and the running processes.</summary>
public sealed class RiskAnalyzer
{
    private readonly IReadOnlyList<InstalledApp> _apps;
    private readonly IReadOnlyList<string> _runningProcessPaths;

    public RiskAnalyzer(IReadOnlyList<InstalledApp> apps, IReadOnlyList<string> runningProcessPaths)
    {
        _apps = apps;
        _runningProcessPaths = runningProcessPaths;
    }

    /// <summary>
    /// Reads the Uninstall registry keys and the main-module path of every process it is allowed to read.
    /// </summary>
    public static RiskAnalyzer ForThisMachine()
    {
        var apps = new AppInventory().ListInstalledApps(measureSizes: false);
        var paths = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var file = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(file))
                {
                    paths.Add(file);
                }
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Protected or already exited: not readable, so it cannot be judged.
            }
            finally
            {
                process.Dispose();
            }
        }

        return new RiskAnalyzer(apps, paths);
    }

    /// <summary>Returns the risk of sweeping <paramref name="path"/> and the reason; the first matching rule wins.</summary>
    public (SweepRisk Risk, string Reason) Evaluate(string path)
    {
        foreach (var process in _runningProcessPaths)
        {
            if (PathGuard.IsSameOrUnder(process, path))
            {
                return (SweepRisk.Locked, $"{Path.GetFileName(process)} is running from this location.");
            }
        }

        foreach (var app in _apps)
        {
            if (!string.IsNullOrWhiteSpace(app.InstallLocation) && PathGuard.IsSameOrUnder(path, app.InstallLocation))
            {
                return (SweepRisk.Caution, $"Inside the install folder of {app.Name}.");
            }
        }

        var segment = FirstSegmentBelowAppData(path);
        if (segment is not null)
        {
            foreach (var app in _apps)
            {
                // Name only: a publisher such as "Microsoft" would otherwise flag all of AppData\Local\Microsoft.
                if (string.Equals(segment, app.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return (SweepRisk.Caution, $"Data that {app.Name} is installed to use; the app recreates it.");
                }
            }
        }

        return PathGuard.LooksLikePersonalData(path)
            ? (SweepRisk.Caution, "Looks like personal data.")
            : (SweepRisk.Safe, string.Empty);
    }

    private static string? FirstSegmentBelowAppData(string path)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root) || !PathGuard.IsSameOrUnder(path, root))
            {
                continue;
            }

            var below = PathGuard.Normalize(path)[PathGuard.Normalize(root).Length..]
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (below.Length > 0)
            {
                return below[0];
            }
        }

        return null;
    }
}
