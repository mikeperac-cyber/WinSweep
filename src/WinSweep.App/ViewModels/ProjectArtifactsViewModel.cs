using System.Collections.ObjectModel;
using WinSweep.Core.Cleanup;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;
using WinSweep.Core.Scanning;
using WinSweep.Core.Settings;
using WinSweep.Core.Sweep;

namespace WinSweep.App.ViewModels;

public sealed class ProjectArtifactsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly OperationLog _log;
    private readonly DockerScanner _docker = new();
    private bool _isBusy;
    private bool _isDockerBusy;
    private string _dockerStatusText = "Check what Docker could reclaim. Nothing is removed until you press Prune.";
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
        DockerItems = new ObservableCollection<DockerItem>();

        _statusText = Roots.Count > 0
            ? "Ready to check these folders. Nothing is removed until you review and confirm it."
            : "Choose project folders to look for rebuildable output.";
    }

    public ObservableCollection<string> Roots { get; }
    public ObservableCollection<ProjectArtifact> Artifacts { get; }
    public ObservableCollection<DockerItem> DockerItems { get; }

    /// <summary>Days a project must have been idle before its build output is offered. Kept between 1 and 365.</summary>
    public int InactivityDays
    {
        get => _settings.ProjectInactivityDays;
        set
        {
            var clamped = Math.Clamp(value, 1, 365);
            if (clamped != _settings.ProjectInactivityDays)
            {
                _settings.ProjectInactivityDays = clamped;
                _settings.Save();
            }

            // Raised even when unchanged so the box snaps back from an out-of-range entry.
            OnPropertyChanged(nameof(InactivityDays));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanScan));
                OnPropertyChanged(nameof(CanUseDocker));
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

    /// <summary>Docker work has its own flag so the project scan screen does not flip while docker.exe runs.</summary>
    public bool CanUseDocker => !IsBusy && !_isDockerBusy;
    public bool HasDockerItems => DockerItems.Count > 0;

    public string DockerStatusText
    {
        get => _dockerStatusText;
        private set => SetProperty(ref _dockerStatusText, value);
    }
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
        : $"{SelectedCount} selected · {ByteSizeFormatter.Format(SelectedBytes)} can be {(_settings.UseQuarantineVault ? "moved to the quarantine vault" : "moved to the Recycle Bin")}";

    public string RemoveButtonText => _settings.UseQuarantineVault ? "Move selected to quarantine" : "Move selected to Recycle Bin";

    /// <summary>Title and text for the confirmation dialog, which depend on where removed folders go.</summary>
    public string ConfirmTitle => _settings.UseQuarantineVault
        ? "Move these build artifacts to the quarantine vault?"
        : "Move these build artifacts to the Recycle Bin?";

    public string ConfirmText => _settings.UseQuarantineVault
        ? $"{SelectedCount} verified, inactive folder(s) ({ByteSizeFormatter.Format(SelectedBytes)}) will be moved to the quarantine vault. You can undo this from History for {VaultDays} days."
        : $"{SelectedCount} verified, inactive folder(s) ({ByteSizeFormatter.Format(SelectedBytes)}) will be moved to the Recycle Bin. You can restore them later if a project still needs them.";

    private int VaultDays => Math.Max(1, _settings.VaultRetentionDays);

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
            var scanner = new ProjectArtifactScanner(projectInactivity: TimeSpan.FromDays(InactivityDays));
            var result = await Task.Run(() => scanner.Scan(
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

    /// <summary>Dry run: builds and saves the manifest for the selected folders and changes nothing.</summary>
    public async Task<SweepManifest?> PreviewAsync()
    {
        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            StatusText = "Select at least one verified artifact.";
            return null;
        }

        try
        {
            var runner = new SweepRunner(_log, _settings);
            return await Task.Run(() => runner.Preview("purge", targets));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"The preview could not be built: {e.Message}";
            return null;
        }
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
            SweepSession? session;
            try
            {
                var runner = new SweepRunner(_log, _settings);
                var targets = SelectedTargets();
                session = await Task.Run(() => runner.BeginAsync("purge", targets));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                StatusText = e.Message;
                return null;
            }

            var service = session is null
                ? new ProjectArtifactService(_log, _settings)
                : new ProjectArtifactService(_log, _settings, session.Quarantine, OperationAction.Quarantined);
            var runId = session?.Manifest.Id ?? Guid.NewGuid().ToString("N")[..8];

            ProjectArtifactCleanupResult result;
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

            foreach (var artifact in selected)
            {
                Artifacts.Remove(artifact);
            }

            var destination = session is null ? "the Recycle Bin" : "the quarantine vault";
            StatusText = result.FailedCount > 0
                ? $"Moved {result.RecycledCount} folder(s) ({ByteSizeFormatter.Format(result.BytesReclaimed)}) to {destination}; {result.FailedCount} could not be moved."
                : $"Moved {result.RecycledCount} folder(s) ({ByteSizeFormatter.Format(result.BytesReclaimed)}) to {destination}.";

            if (session is not null)
            {
                StatusText += $" Undo it from History within {VaultDays} days.";
                if (_settings.SnapshotBeforeSweep && session.Manifest.Snapshot is { Succeeded: false } snapshot)
                {
                    StatusText += $" {snapshot.Message}";
                }
            }

            if (saveProblem is not null)
            {
                StatusText += $" The sweep record could not be saved: {saveProblem}";
            }

            NotifySelectionChanged();
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Asks Docker what it could reclaim. Read-only.</summary>
    public async Task CheckDockerAsync()
    {
        if (!CanUseDocker)
        {
            return;
        }

        SetDockerBusy(true);
        try
        {
            await LoadDockerAsync();
        }
        finally
        {
            SetDockerBusy(false);
        }
    }

    /// <summary>Prunes one kind of Docker leftover, checks again and shows the outcome. The page asks the user first.</summary>
    public async Task PruneDockerAsync(DockerItem item)
    {
        if (!CanUseDocker)
        {
            return;
        }

        SetDockerBusy(true);
        DockerStatusText = $"Pruning {item.Name.ToLowerInvariant()}...";
        try
        {
            var result = await _docker.PruneAsync(item, _log, Guid.NewGuid().ToString("N")[..8]);
            await LoadDockerAsync();
            DockerStatusText = result.Message;
        }
        finally
        {
            SetDockerBusy(false);
        }
    }

    private async Task LoadDockerAsync()
    {
        DockerStatusText = "Asking Docker...";
        var (items, unavailable) = await _docker.ScanAsync();
        DockerItems.Clear();
        foreach (var item in items)
        {
            DockerItems.Add(item);
        }

        OnPropertyChanged(nameof(HasDockerItems));
        DockerStatusText = unavailable ?? (items.Count == 0 ? "Docker has nothing to reclaim." : "Prune removes this Docker data permanently.");
    }

    private void SetDockerBusy(bool value)
    {
        _isDockerBusy = value;
        OnPropertyChanged(nameof(CanUseDocker));
    }

    private List<SweepTarget> SelectedTargets() => Artifacts
        .Where(a => a.IsSelected)
        .Select(a => new SweepTarget(a.Path, $"{a.ProjectName}\\{a.Name}", "Project build output", a.SizeBytes))
        .ToList();

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

        if (settings.ProjectScanRootsConfigured)
        {
            return roots;
        }

        // Suggestions only: they fill the editable list and nothing is scanned until the user presses scan.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var suggestions = new[]
            {
                "source", "Projects", "workspace", "code", "repos", "GitHub",
                @"source\repos", "dev", "Documents"
            }
            .Select(name => Path.Combine(profile, name))
            .Where(Directory.Exists);

        return roots.Concat(suggestions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
