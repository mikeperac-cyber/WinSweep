using System.Collections.ObjectModel;
using WinSweep.Core.Cleanup;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Settings;

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
            }
        }
    }

    public string RemoveButtonText => IsUsingRecycleBin
        ? "Move selected to Recycle Bin"
        : "Delete selected permanently";

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
            var service = new CleanupService(_log, _settings);
            var runId = Guid.NewGuid().ToString("N")[..8];
            var result = await Task.Run(() => service.Execute(selected, runId));

            var removed = Candidates.Where(c => c.IsSelected).ToList();
            foreach (var candidate in removed)
            {
                candidate.PropertyChanged -= Candidate_PropertyChanged;
                Candidates.Remove(candidate);
            }

            OnPropertyChanged(nameof(HasCandidates));

            StatusText = result.FailedCount > 0
                ? $"Removed {result.RemovedCount} locations ({ByteSizeFormatter.Format(result.BytesReclaimed)}); {result.FailedCount} could not be removed."
                : $"Removed {result.RemovedCount} locations, reclaiming {ByteSizeFormatter.Format(result.BytesReclaimed)}.";

            OnPropertyChanged(nameof(TotalSizeText));
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Candidate_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupCandidate.IsSelected))
        {
            OnPropertyChanged(nameof(TotalSizeText));
        }
    }
}
