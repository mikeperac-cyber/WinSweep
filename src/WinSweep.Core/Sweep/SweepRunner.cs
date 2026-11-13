using WinSweep.Core.Cleanup;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Settings;

namespace WinSweep.Core.Sweep;

/// <summary>
/// The one entry point the UI uses for dry runs, vault sweeps, undo and clean-up of old sweeps.
/// The actual removal stays in the cleanup services, which take <see cref="SweepSession.Quarantine"/> as their remover.
/// </summary>
public sealed class SweepRunner
{
    private readonly OperationLog _log;
    private readonly AppSettings _settings;
    private readonly QuarantineVault _vault;
    private readonly Func<bool> _isElevated;
    private readonly string _dryRunManifestPath;
    private RiskAnalyzer? _analyzer;

    public SweepRunner(
        OperationLog log,
        AppSettings settings,
        QuarantineVault? vault = null,
        Func<bool>? isElevated = null,
        RiskAnalyzer? analyzer = null,
        string? dryRunManifestPath = null)
    {
        _log = log;
        _settings = settings;
        _vault = vault ?? new QuarantineVault();
        _isElevated = isElevated ?? CleanupScanner.IsElevated;
        _analyzer = analyzer;
        _dryRunManifestPath = dryRunManifestPath ?? SweepPlanner.DryRunManifestPath;
    }

    private RiskAnalyzer Analyzer => _analyzer ??= RiskAnalyzer.ForThisMachine();

    /// <summary>Dry run: builds the manifest, saves it to the dry-run path, changes nothing else.</summary>
    public SweepManifest Preview(string source, IEnumerable<SweepTarget> targets)
    {
        var manifest = SweepPlanner.Build(source, targets, Analyzer, dryRun: true);
        try
        {
            manifest.Save(_dryRunManifestPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The saved copy is only for the user to read; the preview itself is still valid.
        }

        return manifest;
    }

    /// <summary>
    /// Real sweep: null when the vault is switched off. Otherwise builds the manifest, takes the snapshot
    /// when settings.SnapshotBeforeSweep, and opens the vault session. Throws one IOException when the vault cannot be opened.
    /// </summary>
    public async Task<SweepSession?> BeginAsync(string source, IEnumerable<SweepTarget> targets,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.UseQuarantineVault)
        {
            return null;
        }

        var manifest = SweepPlanner.Build(source, targets, Analyzer, dryRun: false);
        if (_settings.SnapshotBeforeSweep)
        {
            manifest.Snapshot = await VolumeSnapshot.CreateAsync(_isElevated(), cancellationToken);
        }

        try
        {
            return _vault.Begin(manifest);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The quarantine vault could not be opened: {e.Message}", e);
        }
    }

    /// <summary>Every sweep in the vault, newest first.</summary>
    public IReadOnlyList<SweepManifest> History() => _vault.List();

    /// <summary>The newest sweep that still has a quarantined entry, or null.</summary>
    public string? LastSweepId =>
        History().FirstOrDefault(m => m.Entries.Any(e => e.Status == SweepEntryStatus.Quarantined))?.Id;

    /// <summary>Restores a sweep and logs each restored entry and each problem.</summary>
    public RestoreResult Rollback(string sweepId, CancellationToken cancellationToken = default)
    {
        var wasQuarantined = History().FirstOrDefault(m => m.Id == sweepId)?.Entries
            .Where(e => e.Status == SweepEntryStatus.Quarantined).Select(e => e.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();

        var result = _vault.Restore(sweepId, cancellationToken);

        var after = History().FirstOrDefault(m => m.Id == sweepId);
        foreach (var entry in after?.Entries.Where(e => wasQuarantined.Contains(e.Path)) ?? Enumerable.Empty<SweepEntry>())
        {
            if (entry.Status == SweepEntryStatus.Restored)
            {
                LogRollback(sweepId, OperationAction.Restored, entry.Path, string.Empty, entry.SizeBytes);
            }
            else if (entry.Detail.Length > 0 && !result.Problems.Contains($"{entry.Path}: {entry.Detail}"))
            {
                // Some files already existed: the archive is kept, so this is not a restore.
                LogRollback(sweepId, OperationAction.Skipped, entry.Path, entry.Detail, 0);
            }
        }

        foreach (var problem in result.Problems)
        {
            LogRollback(sweepId, OperationAction.Failed, sweepId, problem, 0);
        }

        return result;
    }

    /// <summary>Permanently deletes one quarantined sweep. Returns false when it does not exist or cannot be removed.</summary>
    public bool DeleteSweep(string sweepId)
    {
        if (!_vault.Delete(sweepId))
        {
            return false;
        }

        LogVaultDelete(sweepId, "Quarantined sweep deleted permanently");
        return true;
    }

    /// <summary>Deletes sweeps older than the retention setting (at least one day). Returns how many went.</summary>
    public int PurgeExpired()
    {
        var before = History().Select(m => m.Id).ToList();
        var removed = _vault.PurgeExpired(TimeSpan.FromDays(Math.Max(1, _settings.VaultRetentionDays)));
        if (removed > 0)
        {
            var left = History().Select(m => m.Id).ToHashSet();
            foreach (var id in before.Where(id => !left.Contains(id)))
            {
                LogVaultDelete(id, "Expired quarantined sweep deleted permanently");
            }
        }

        return removed;
    }

    private void LogVaultDelete(string sweepId, string detail) => _log.Append(new OperationRecord
    {
        Command = "vault",
        RunId = sweepId,
        Action = OperationAction.Removed,
        Path = sweepId,
        Detail = detail
    });

    private void LogRollback(string sweepId, OperationAction action, string path, string detail, long size) =>
        _log.Append(new OperationRecord
        {
            Command = "rollback",
            RunId = sweepId,
            Action = action,
            Path = path,
            Detail = detail,
            SizeBytes = size
        });
}
