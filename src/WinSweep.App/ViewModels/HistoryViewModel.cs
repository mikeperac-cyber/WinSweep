using System.Collections.ObjectModel;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Settings;
using WinSweep.Core.Sweep;

namespace WinSweep.App.ViewModels;

/// <summary>One row of "Sweeps you can undo".</summary>
public sealed class SweepHistoryItem
{
    public required string Id { get; init; }
    public required string WhenText { get; init; }
    public required string SourceText { get; init; }
    public required string SizeText { get; init; }
    public required string StatusText { get; init; }

    /// <summary>The snapshot message of the sweep, or empty when no snapshot was asked for.</summary>
    public string SnapshotText { get; init; } = string.Empty;
    public bool HasSnapshotText => SnapshotText.Length > 0;

    /// <summary>True while at least one entry of the sweep is still in the vault.</summary>
    public bool CanRestore { get; init; }
}

public sealed class HistoryViewModel : ObservableObject
{
    private readonly OperationLog _log;
    private readonly SweepRunner _runner;
    private string _statusText = string.Empty;
    private bool _isBusy;

    public HistoryViewModel(OperationLog log, AppSettings settings)
    {
        _log = log;
        _runner = new SweepRunner(log, settings);
        Records = new ObservableCollection<OperationRecord>();
        Sweeps = new ObservableCollection<SweepHistoryItem>();
    }

    public ObservableCollection<OperationRecord> Records { get; }
    public ObservableCollection<SweepHistoryItem> Sweeps { get; }

    public bool HasSweeps => Sweeps.Count > 0;
    public bool HasUndoableSweep => Sweeps.Any(s => s.CanRestore);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void Refresh()
    {
        LoadLists();

        StatusText = Records.Count == 0
            ? "No actions have been recorded yet."
            : $"{Records.Count} actions recorded.";
    }

    /// <summary>Restores one quarantined sweep. Files that already exist are never overwritten.</summary>
    public async Task RestoreAsync(string id)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        StatusText = "Restoring...";
        try
        {
            var result = await Task.Run(() => _runner.Rollback(id));

            var text = result.Restored > 0 ? $"Restored {result.Restored} locations." : string.Empty;
            if (result.Restored > 0 && result.SkippedExisting > 0)
            {
                text += $" {result.SkippedExisting} files were left alone because they already exist.";
            }

            if (result.Problems.Count > 0)
            {
                text += $" {result.Problems[0]}";
            }

            LoadLists();
            StatusText = text.Trim().Length > 0 ? text.Trim() : "Nothing was restored.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LoadLists();
            StatusText = $"The restore could not finish: {e.Message}";
        }
        finally
        {
            _isBusy = false;
        }
    }

    /// <summary>Restores the newest sweep that still has something in quarantine.</summary>
    public async Task UndoLastAsync()
    {
        var id = _runner.LastSweepId;
        if (id is null)
        {
            StatusText = "There is no sweep to undo.";
            return;
        }

        await RestoreAsync(id);
    }

    /// <summary>Permanently deletes a quarantined sweep. The page asks the user first.</summary>
    public void Delete(string id)
    {
        if (_isBusy)
        {
            return;
        }

        var deleted = _runner.DeleteSweep(id);
        LoadLists();
        StatusText = deleted
            ? "The sweep was deleted permanently."
            : "That sweep could not be deleted.";
    }

    private void LoadLists()
    {
        Records.Clear();
        foreach (var record in _log.Read(500))
        {
            Records.Add(record);
        }

        Sweeps.Clear();
        foreach (var manifest in _runner.History())
        {
            Sweeps.Add(ToItem(manifest));
        }

        OnPropertyChanged(nameof(HasSweeps));
        OnPropertyChanged(nameof(HasUndoableSweep));
    }

    private static SweepHistoryItem ToItem(SweepManifest manifest)
    {
        var inVault = manifest.Entries.Count(e => e.Status == SweepEntryStatus.Quarantined);
        var restored = manifest.Entries.Count(e => e.Status == SweepEntryStatus.Restored);

        // A restore that could not finish leaves those entries quarantined, so they count as problems.
        var status = (inVault, restored) switch
        {
            (0, 0) => "Nothing was quarantined",
            (0, _) => "Restored",
            (_, 0) => $"{inVault} locations in quarantine",
            (1, _) => "Partly restored — 1 problem",
            _ => $"Partly restored — {inVault} problems"
        };

        return new SweepHistoryItem
        {
            Id = manifest.Id,
            WhenText = manifest.CreatedAt.LocalDateTime.ToString("g"),
            SourceText = manifest.Source == "purge" ? "Project build output" : "Clean",
            SizeText = ByteSizeFormatter.Format(manifest.TotalBytes),
            StatusText = status,
            SnapshotText = manifest.Snapshot?.Message ?? string.Empty,
            CanRestore = inVault > 0
        };
    }
}
