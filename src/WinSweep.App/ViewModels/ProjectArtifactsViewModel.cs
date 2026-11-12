using System.Collections.ObjectModel;
using WinSweep.Core.Cleanup;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;
using WinSweep.Core.Scanning;
using WinSweep.Core.Settings;

namespace WinSweep.App.ViewModels;

public sealed class ProjectArtifactsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly OperationLog _log;
    private readonly ProjectArtifactScanner _scanner = new();
    private bool _isBusy;
    private bool _hasScanned;
    private string _statusText = "Choose project folders to look for rebuildable output.";
    private string _progressText = string.Empty;
    private CancellationTokenSource? _scanCancellation;

    public ProjectArtifactsViewModel(AppSettings settings, OperationLog log)
    {
        _settings = settings;
        _log = log;
        Roots = new ObservableCollection<string>(GetRoots(settings));
        Artifacts = new ObservableCollection<ProjectArtifact>();

        _statusText = Roots.Count > 0
            ? "Ready to check these folders. Nothing is removed until you review and confirm it."
            : "Choose project folders to look for rebuildable output.";
    }

    public ObservableCollection<string> Roots { get; }
    public ObservableCollection<ProjectArtifact> Artifacts { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanScan));
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

    public bool CanScan => Roots.Count > 0 && !IsBusy;
    public bool EmptyStateVisible => !HasScanned && !IsBusy;
    public bool ResultsVisible => HasScanned && !IsBusy;
    public bool HasArtifacts => Artifacts.Count > 0;
    public bool HasRoots => Roots.Count > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string RootSummary => Roots.Count switch
    {
        0 => "No project folders selected",
        1 => "1 project folder",
        _ => $"{Roots.Count} project folders"
    };

    public int SelectedCount => Artifacts.Count(a => a.IsSelected);

    public long SelectedBytes => Artifacts.Where(a => a.IsSelected).Sum(a => a.SizeBytes);

    public string SelectedSummary => SelectedCount == 0
        ? "Nothing selected"
        : $"{SelectedCount} selected · {ByteSizeFormatter.Format(SelectedBytes)} can be moved to the Recycle Bin";

    public void AddRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized;
        try
        {
            normalized = PathGuard.Normalize(path);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            StatusText = "That folder path could not be read.";
            return;
        }

        if (!Directory.Exists(normalized))
        {
            StatusText = "That folder no longer exists.";
            return;
        }

        if (PathGuard.IsRoot(normalized) || PathGuard.IsProtectedLocation(normalized) || PathGuard.HasReparsePointInPath(normalized))
        {
            StatusText = "Choose a normal project folder. Drive roots, system folders and linked paths are skipped.";
            return;
        }

        if (Roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            StatusText = "That folder is already in the scan list.";
            return;
        }

        Roots.Add(normalized);
        PersistRoots();
        StatusText = $"Added {normalized}.";
        OnPropertyChanged(nameof(RootSummary));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(HasRoots));
    }

    public void RemoveRoot(string path)
    {
        var existing = Roots.FirstOrDefault(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        Roots.Remove(existing);
        PersistRoots();
        StatusText = $"Removed {existing} from the scan list.";
        OnPropertyChanged(nameof(RootSummary));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(HasRoots));
    }

    public async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (Roots.Count == 0)
        {
            StatusText = "Add one or more project folders first.";
            return;
        }

        IsBusy = true;
        HasScanned = false;
        PersistRoots();
        Artifacts.Clear();
        ProgressText = "Starting the scan...";
        _scanCancellation = new CancellationTokenSource();

        try
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var roots = Roots.ToList();
            var result = await Task.Run(() => _scanner.Scan(
                roots,
                progress => dispatcher.TryEnqueue(() =>
                {
                    ProgressText = progress.Message;
                    StatusText = progress.Message;
                }),
                _scanCancellation.Token));

            foreach (var artifact in result.Artifacts)
            {
                artifact.PropertyChanged += Artifact_PropertyChanged;
                Artifacts.Add(artifact);
            }

            HasScanned = true;
            StatusText = BuildResultSummary(result);
            ProgressText = string.Empty;
            NotifySelectionChanged();
        }
        catch (OperationCanceledException)
        {
            StatusText = "The scan was cancelled. No files were changed.";
            ProgressText = string.Empty;
            HasScanned = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = $"The scan could not finish: {e.Message}";
            ProgressText = string.Empty;
            HasScanned = false;
        }
        catch (Exception e)
        {
            StatusText = $"The scan stopped safely: {e.Message}";
            ProgressText = string.Empty;
            HasScanned = false;
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            IsBusy = false;
        }
    }

    public void CancelScan()
    {
        _scanCancellation?.Cancel();
        ProgressText = "Stopping after the current folder...";
    }

    public void SelectAllSafe()
    {
        foreach (var artifact in Artifacts)
        {
            artifact.IsSelected = artifact.BlockedReason is null && artifact.SizeStatus == ScanStatus.Complete;
        }

        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
        foreach (var artifact in Artifacts)
        {
            artifact.IsSelected = false;
        }

        NotifySelectionChanged();
    }

    public async Task<ProjectArtifactCleanupResult?> RemoveSelectedAsync()
    {
        var selected = Artifacts.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = "Select at least one verified artifact.";
            return null;
        }

        IsBusy = true;
        try
        {
            var service = new ProjectArtifactService(_log, _settings);
            var runId = Guid.NewGuid().ToString("N")[..8];
            var result = await Task.Run(() => service.Execute(selected, runId));

            foreach (var artifact in selected)
            {
                Artifacts.Remove(artifact);
            }

            StatusText = result.FailedCount > 0
                ? $"Moved {result.RecycledCount} folder(s) ({ByteSizeFormatter.Format(result.BytesReclaimed)}) to the Recycle Bin; {result.FailedCount} could not be moved."
                : $"Moved {result.RecycledCount} folder(s) ({ByteSizeFormatter.Format(result.BytesReclaimed)}) to the Recycle Bin.";

            NotifySelectionChanged();
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Artifact_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectArtifact.IsSelected))
        {
            NotifySelectionChanged();
        }
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(HasArtifacts));
        OnPropertyChanged(nameof(ResultsVisible));
    }

    private void PersistRoots()
    {
        _settings.ProjectScanRoots = Roots.ToList();
        _settings.ProjectScanRootsConfigured = true;
        _settings.Save();
    }

    private static IReadOnlyList<string> GetRoots(AppSettings settings)
    {
        var roots = settings.ProjectScanRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (settings.ProjectScanRootsConfigured || roots.Count > 0)
        {
            return roots;
        }

        // Suggest only conventional source-code folders. Do not scan Documents,
        // Desktop, Downloads, OneDrive, or the whole profile without the user's say-so.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var suggestions = new[] { "source", "Projects", "workspace", "code", "repos", "GitHub" }
            .Select(name => Path.Combine(profile, name))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return suggestions;
    }

    private static string BuildResultSummary(ProjectArtifactScanResult result)
    {
        var totalBytes = result.Artifacts.Sum(a => a.SizeBytes);
        if (result.Artifacts.Count == 0)
        {
            return result.IsPartial
                ? "No build artifacts found in the folders that could be checked. Some folders were skipped."
                : "No recognized build artifacts found in these project folders.";
        }

        var summary = $"Found {result.Artifacts.Count} candidate(s) · {ByteSizeFormatter.Format(totalBytes)} measured";
        if (result.IsPartial)
        {
            summary += result.TimedOut
                ? " · scan time limit reached; unmeasured folders were left alone"
                : " · some folders could not be checked";
        }

        return summary;
    }
}
