using System.Collections.ObjectModel;
using WinSweep.Core.Models;
using WinSweep.Core.Scanning;

namespace WinSweep.App.ViewModels;

public sealed class DiskViewModel : ObservableObject
{
    private readonly DiskScanner _scanner = new();
    private string _currentPath = string.Empty;
    private string _statusText = "Choose a folder to explore.";
    private bool _isBusy;
    private bool _hasScanned;
    private long _totalBytes;
    private ScanStatus _totalStatus = ScanStatus.Unavailable;

    public DiskViewModel()
    {
        Entries = new ObservableCollection<DiskEntry>();
        LargeFiles = new ObservableCollection<DiskEntry>();
        CurrentPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public ObservableCollection<DiskEntry> Entries { get; }
    public ObservableCollection<DiskEntry> LargeFiles { get; }

    public string CurrentPath
    {
        get => _currentPath;
        set => SetProperty(ref _currentPath, value);
    }

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

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string TotalText => _totalStatus == ScanStatus.Unavailable
        ? "Size unknown"
        : $"{Core.Models.ByteSizeFormatter.Format(_totalBytes)} in this folder";

    public bool CanGoUp => !string.IsNullOrEmpty(CurrentPath) &&
                            !string.Equals(CurrentPath.TrimEnd('\\'), Path.GetPathRoot(CurrentPath)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public async Task ScanAsync(string? path = null)
    {
        if (IsBusy)
        {
            return;
        }

        var target = path ?? CurrentPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusText = "Choose a folder to explore.";
            return;
        }

        IsBusy = true;
        HasScanned = false;
        StatusText = "Scanning...";
        try
        {
            var result = await Task.Run(() => _scanner.Scan(target));

            CurrentPath = result.Path;
            _totalBytes = result.TotalSizeBytes;
            _totalStatus = result.Status;

            Entries.Clear();
            foreach (var entry in result.Entries)
            {
                Entries.Add(entry);
            }

            LargeFiles.Clear();
            foreach (var file in result.LargeFiles)
            {
                LargeFiles.Add(file);
            }

            StatusText = result.Status switch
            {
                ScanStatus.Complete => $"Scanned {result.TotalFileCount} files in {result.Duration.TotalSeconds:0.0}s.",
                ScanStatus.Partial when result.TimedOut && result.UnreadablePaths.Count == 0 =>
                    "Partial scan: the scan ran out of time before every folder was measured. The size is a lower bound.",
                ScanStatus.Partial when result.TimedOut =>
                    $"Partial scan: the scan ran out of time and {result.UnreadablePaths.Count} paths could not be read. The size is a lower bound.",
                ScanStatus.Partial =>
                    $"Partial scan: {result.UnreadablePaths.Count} paths could not be read. The size is a lower bound.",
                _ => "This folder could not be read."
            };

            HasScanned = true;

            OnPropertyChanged(nameof(TotalText));
            OnPropertyChanged(nameof(CanGoUp));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = $"The folder could not be scanned: {e.Message}";
            HasScanned = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task DrillIntoAsync(DiskEntry entry)
    {
        if (!entry.IsDirectory)
        {
            return Task.CompletedTask;
        }

        return ScanAsync(entry.Path);
    }

    public Task GoUpAsync()
    {
        var parent = Path.GetDirectoryName(CurrentPath.TrimEnd('\\'));
        if (string.IsNullOrEmpty(parent))
        {
            return Task.CompletedTask;
        }

        return ScanAsync(parent);
    }
}
