using System.Collections.ObjectModel;
using WinSweep.Core.Cleanup;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Settings;
using WinSweep.Core.Sweep;

namespace WinSweep.App.ViewModels;

public sealed class CleanViewModel : ObservableObject
{
    private readonly OperationLog _log;
    private readonly AppSettings _settings;
    private bool _isBusy;
    private bool _hasScanned;
    private string _statusText = "Scan to find disposable files on this machine.";
    private bool _isUsingRecycleBin = true;

    public CleanViewModel(OperationLog log, AppSettings settings)
    {
        _log = log;
        _settings = settings;
        _isUsingRecycleBin = settings.UseRecycleBin;
        Candidates = new ObservableCollection<CleanupCandidate>();
    }

    public ObservableCollection<CleanupCandidate> Candidates { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(EmptyStateVisible));
                OnPropertyChanged(nameof(ResultsVisible));
            }
        }
    }

    public bool HasScanned
    {
        get => _hasScanned;
        private set
        {
            if (SetProperty(ref _hasScanned, value))
            {
                OnPropertyChanged(nameof(EmptyStateVisible));
                OnPropertyChanged(nameof(ResultsVisible));
            }
        }
    }

    public bool EmptyStateVisible => !HasScanned && !IsBusy;
    public bool ResultsVisible => HasScanned && !IsBusy;
    public bool HasCandidates => Candidates.Count > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsUsingRecycleBin
    {
        get => _isUsingRecycleBin;
        set
        {
            if (SetProperty(ref _isUsingRecycleBin, value))
            {
                _settings.UseRecycleBin = value;
                _settings.Save();
                OnPropertyChanged(nameof(RemoveButtonText));
                OnPropertyChanged(nameof(RemovalModeText));
            }
        }
    }

    public string RemoveButtonText => _settings.UseQuarantineVault
        ? "Move selected to quarantine"
        : IsUsingRecycleBin
            ? "Move selected to Recycle Bin"
            : "Delete selected permanently";

    /// <summary>What happens to removed locations, as the end of a sentence for the confirmation dialog.</summary>
    public string RemovalModeText => _settings.UseQuarantineVault
        ? $"moved to the quarantine vault. You can undo this from History for {VaultDays} days."
        : IsUsingRecycleBin
            ? "moved to the Recycle Bin, so you can still recover them."
            : "deleted permanently. This cannot be undone.";

    private int VaultDays => Math.Max(1, _settings.VaultRetentionDays);

    public string TotalSizeText
    {
        get
        {
            var selected = Candidates.Where(c => c.IsSelected).Sum(c => c.SizeBytes);
            return $"{ByteSizeFormatter.Format(selected)} selected of {ByteSizeFormatter.Format(Candidates.Sum(c => c.SizeBytes))}";
        }
    }

    public async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        HasScanned = false;
        StatusText = "Scanning for disposable files...";
        Candidates.Clear();
        OnPropertyChanged(nameof(TotalSizeText));

        try
        {
            var scanner = new CleanupScanner();
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            var found = await Task.Run(() => scanner.Scan(
                _settings,
                onFound: candidate => dispatcher.TryEnqueue(() =>
                {
                    Candidates.Add(candidate);
                    OnPropertyChanged(nameof(TotalSizeText));
                })));

            // The scan reports candidates as it finds them; this pass keeps the list
            // ordered by size once everything is in.
            var ordered = found.OrderByDescending(c => c.SizeBytes).ToList();
            Candidates.Clear();
            foreach (var candidate in ordered)
            {
                candidate.PropertyChanged += Candidate_PropertyChanged;
                Candidates.Add(candidate);
            }

            StatusText = Candidates.Count == 0
                ? "Nothing to clean was found."
                : $"{Candidates.Count} locations found. Review them, then remove the ones you want.";
            HasScanned = true;
            OnPropertyChanged(nameof(HasCandidates));
            OnPropertyChanged(nameof(TotalSizeText));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = $"The scan could not finish: {e.Message}";
            HasScanned = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Dry run: builds and saves the manifest for the selected locations and changes nothing.</summary>
    public async Task<SweepManifest?> PreviewAsync()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            StatusText = "Nothing is selected.";
            return null;
        }

        try
        {
            var runner = new SweepRunner(_log, _settings);
            return await Task.Run(() => runner.Preview("clean", targets));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = $"The preview could not be built: {e.Message}";
            return null;
        }
    }

    public async Task<CleanupResult?> RemoveSelectedAsync()
    {
        var selected = Candidates.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = "Nothing is selected.";
            return null;
        }

        IsBusy = true;
        StatusText = $"Removing {selected.Count} locations...";
        try
        {
            SweepSession? session;
            try
            {
                var runner = new SweepRunner(_log, _settings);
                var targets = SelectedTargets();
                session = await Task.Run(() => runner.BeginAsync("clean", targets));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                StatusText = e.Message;
                return null;
            }

            var service = session is null
                ? new CleanupService(_log, _settings)
                : new CleanupService(_log, _settings, session.Quarantine, OperationAction.Quarantined);
            var runId = session?.Manifest.Id ?? Guid.NewGuid().ToString("N")[..8];

            CleanupResult result;
            string? saveProblem = null;
            try
            {
                result = await Task.Run(() => service.Execute(selected, runId));
            }
            finally
            {
                try
                {
                    session?.Complete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    saveProblem = e.Message;
                }
            }

            var removed = Candidates.Where(c => c.IsSelected).ToList();
            foreach (var candidate in removed)
            {
                candidate.PropertyChanged -= Candidate_PropertyChanged;
                Candidates.Remove(candidate);
            }

            OnPropertyChanged(nameof(HasCandidates));

            if (session is not null)
            {
                StatusText = $"Moved {result.RemovedCount} locations ({ByteSizeFormatter.Format(result.BytesReclaimed)}) to the quarantine vault. Undo it from History within {VaultDays} days.";
                if (result.FailedCount > 0)
                {
                    StatusText += $" {result.FailedCount} could not be moved.";
                }

                if (_settings.SnapshotBeforeSweep && session.Manifest.Snapshot is { Succeeded: false } snapshot)
                {
                    StatusText += $" {snapshot.Message}";
                }
            }
            else
            {
                StatusText = result.FailedCount > 0
                    ? $"Removed {result.RemovedCount} locations ({ByteSizeFormatter.Format(result.BytesReclaimed)}); {result.FailedCount} could not be removed."
                    : $"Removed {result.RemovedCount} locations, reclaiming {ByteSizeFormatter.Format(result.BytesReclaimed)}.";
            }

            if (saveProblem is not null)
            {
                StatusText += $" The sweep record could not be saved: {saveProblem}";
            }

            OnPropertyChanged(nameof(TotalSizeText));
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private List<SweepTarget> SelectedTargets() => Candidates
        .Where(c => c.IsSelected)
        .Select(c => new SweepTarget(c.Path, c.Name, c.Category, c.SizeBytes))
        .ToList();

    private void Candidate_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupCandidate.IsSelected))
        {
            OnPropertyChanged(nameof(TotalSizeText));
        }
    }
}
