using System.Diagnostics;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;
using WinSweep.Core.Scanning;
using WinSweep.Core.Settings;

namespace WinSweep.Core.Cleanup;

/// <summary>Finds the cleanup candidates on this machine and explains each one.</summary>
public sealed class CleanupScanner
{
    private readonly TimeSpan _budget;
    private readonly TimeSpan _perCandidateBudget;

    public CleanupScanner(TimeSpan? budget = null, TimeSpan? perCandidateBudget = null)
    {
        _budget = budget ?? TimeSpan.FromSeconds(30);
        _perCandidateBudget = perCandidateBudget ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// Finds the cleanup candidates. Each candidate is reported as soon as it is measured,
    /// so the user sees results while the rest of the scan is still running.
    /// </summary>
    public IReadOnlyList<CleanupCandidate> Scan(
        AppSettings settings,
        bool includeDisabledRules = false,
        Action<CleanupCandidate>? onFound = null,
        CancellationToken cancellationToken = default)
    {
        var runningProcesses = GetRunningProcessNames();
        var deadline = DateTimeOffset.UtcNow + _budget;

        var work = new List<(CleanupRule Rule, string Path)>();
        foreach (var rule in CleanupCatalog.GetRules())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!includeDisabledRules && !rule.EnabledByDefault)
            {
                continue;
            }

            if (rule.RequiresElevation && !IsElevated())
            {
                continue;
            }

            foreach (var path in PathTemplateExpander.Expand(rule.PathTemplate, rule.TargetsFiles))
            {
                work.Add((rule, path));
            }
        }

        var candidates = new List<CleanupCandidate>();
        var sync = new object();

        Parallel.ForEach(work, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2)
        }, item =>
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                return;
            }

            var candidate = BuildCandidate(item.Rule, item.Path, settings, runningProcesses);
            if (candidate is null)
            {
                return;
            }

            lock (sync)
            {
                candidates.Add(candidate);
            }

            onFound?.Invoke(candidate);
        });

        try
        {
            foreach (var game in GameRedistScanner.Scan())
            {
                if (settings.IsProtected(game.Path))
                {
                    continue;
                }

                candidates.Add(game);
                onFound?.Invoke(game);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A game launcher that cannot be read contributes nothing.
        }

        return candidates
            .OrderByDescending(c => c.SizeBytes)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private CleanupCandidate? BuildCandidate(
        CleanupRule rule,
        string path,
        AppSettings settings,
        HashSet<string> runningProcesses)
    {
        var isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path))
        {
            return null;
        }

        var verdict = PathGuard.Evaluate(path);
        if (!verdict.Allowed)
        {
            // A protected or linked location is never offered for removal.
            return null;
        }

        if (settings.IsProtected(path))
        {
            return null;
        }

        SizeMeasurement measurement;
        DateTime? lastWrite;
        if (isFile)
        {
            measurement = DirectorySizer.MeasureFile(path);
            lastWrite = SafeLastWrite(path);
        }
        else
        {
            measurement = DirectorySizer.Measure(path, default, _perCandidateBudget);
            lastWrite = SafeLastWrite(path);
        }

        string? blockedReason = null;
        foreach (var process in rule.BlockingProcesses)
        {
            if (runningProcesses.Contains(process))
            {
                blockedReason = $"{process} is running, so this stays untouched until it closes";
                break;
            }
        }

        var oldEnough = rule.MinimumAgeDays <= 0 ||
                        lastWrite is null ||
                        DateTime.UtcNow - lastWrite.Value >= TimeSpan.FromDays(rule.MinimumAgeDays);

        return new CleanupCandidate
        {
            Name = rule.Name,
            Path = path,
            Category = rule.Category,
            Description = rule.Description,
            Risk = rule.Risk,
            MinimumAgeDays = rule.MinimumAgeDays,
            SizeBytes = measurement.Bytes,
            SizeStatus = measurement.Status,
            LastWriteTimeUtc = lastWrite,
            BlockedReason = blockedReason,
            DeferredReason = oldEnough
                ? null
                : $"Changed recently, so it stays until it has been unused for {rule.MinimumAgeDays} day(s).",
            IsSelected = oldEnough && blockedReason is null && rule.Risk != CleanupRisk.Protected
        };
    }

    private static DateTime? SafeLastWrite(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return File.GetLastWriteTimeUtc(path);
            }

            return Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HashSet<string> GetRunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // If the process list cannot be read, no target is treated as blocked.
        }

        return names;
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
