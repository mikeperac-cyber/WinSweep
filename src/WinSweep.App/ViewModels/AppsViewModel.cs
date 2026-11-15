using System.Collections.ObjectModel;
using WinSweep.Core.Apps;
using WinSweep.Core.Logging;
using WinSweep.Core.Maintenance;
using WinSweep.Core.Models;
using WinSweep.Core.Settings;

namespace WinSweep.App.ViewModels;

public sealed class AppsViewModel : ObservableObject
{
    private readonly OperationLog _log;
    private readonly AppSettings _settings;
    private readonly AppInventory _inventory = new();
    private readonly LeftoverScanner _leftovers = new();
    private readonly AppUpdateService _updater;
    private List<InstalledApp> _allApps = new();
    private bool _isBusy;
    private bool _isUpdating;
    private bool _hasCheckedForUpdates;
    private bool _canCancel;
    private DateTimeOffset? _lastCheckedAt;
    private CancellationTokenSource? _updatesCancellation;
    private string _searchText = string.Empty;
    private string _statusText = "Loading installed applications...";
    private InstalledApp? _selectedApp;

    public AppsViewModel(OperationLog log, AppSettings settings)
    {
        _log = log;
        _settings = settings;
        Apps = new ObservableCollection<InstalledApp>();
        Leftovers = new ObservableCollection<LeftoverItem>();
        Updates = new ObservableCollection<UpdateRow>();
        Updates.CollectionChanged += (_, _) => NotifyUpdatesChanged();
        _updater = new AppUpdateService(log);
    }

    public ObservableCollection<InstalledApp> Apps { get; }
    public ObservableCollection<LeftoverItem> Leftovers { get; }

    /// <summary>Apps that winget can update, filled by <see cref="CheckUpdatesAsync"/>.</summary>
    public ObservableCollection<UpdateRow> Updates { get; }

    public bool HasUpdates => Updates.Count > 0;

    /// <summary>True while at least one listed row still needs work.</summary>
    public bool HasPendingUpdates => Updates.Any(u => u.IsPending);

    /// <summary>True only when the bulk button can actually start work.</summary>
    public bool CanUpdateAll => IsIdle && HasPendingUpdates;

    /// <summary>How many listed rows still need work; drives the header and the bulk button.</summary>
    public int PendingUpdateCount => Updates.Count(u => u.IsPending);

    /// <summary>False while any work is running, so buttons disable instead of ignoring clicks.</summary>
    public bool IsIdle => !_isBusy;

    /// <summary>True only while an update (not a list load) is running.</summary>
    public bool IsUpdating
    {
        get => _isUpdating;
        private set => SetProperty(ref _isUpdating, value);
    }

    /// <summary>True while a check or an update is running and can still be stopped.</summary>
    public bool CanCancel => _canCancel;

    /// <summary>True after a check that finished cleanly with nothing to update.</summary>
    public bool ShowNoUpdates => _hasCheckedForUpdates && !_isBusy && Updates.Count == 0;

    public string UpdatesHeaderText => HasPendingUpdates
        ? $"{PendingUpdateCount} update(s) available"
        : "All listed apps are up to date";

    public string LastCheckedText => _lastCheckedAt is null
        ? string.Empty
        : $"Checked {_lastCheckedAt.Value.ToLocalTime():HH:mm}";

    private void NotifyUpdatesChanged()
    {
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(HasPendingUpdates));
        OnPropertyChanged(nameof(PendingUpdateCount));
        OnPropertyChanged(nameof(ShowNoUpdates));
        OnPropertyChanged(nameof(UpdatesHeaderText));
        OnPropertyChanged(nameof(CanUpdateAll));
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(NoAppsMatch));
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(ShowNoUpdates));
                OnPropertyChanged(nameof(CanUpdateAll));
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public InstalledApp? SelectedApp
    {
        get => _selectedApp;
        set
        {
            if (SetProperty(ref _selectedApp, value))
            {
                OnPropertyChanged(nameof(SelectedAppName));
                OnPropertyChanged(nameof(UninstallCommandText));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedAppIsStore));
            }
        }
    }

    public bool HasSelection => SelectedApp is not null;

    /// <summary>True when a search is active but no installed app matches it.</summary>
    public bool NoAppsMatch =>
        !IsBusy && !string.IsNullOrWhiteSpace(SearchText) && Apps.Count == 0;

    /// <summary>True when the selected app came from the Microsoft Store.</summary>
    public bool SelectedAppIsStore => SelectedApp?.Source == InstallSource.WindowsStore;

    public string SelectedAppName => SelectedApp is null
        ? "No application selected"
        : $"{SelectedApp.Name} · {SelectedApp.SizeDisplay}";

    public string UninstallCommandText => SelectedApp is null
        ? string.Empty
        : SelectedAppIsStore
            ? "Microsoft Store applications are removed by Windows itself, from Settings."
            : string.IsNullOrWhiteSpace(SelectedApp.UninstallString)
                ? "This app has no registered uninstaller."
                : SelectedApp.UninstallString;

    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Loading installed applications...";
        try
        {
            var apps = await Task.Run(() => _inventory.ListAllApps(_settings.MeasureAppSizes));
            _allApps = apps.ToList();
            ApplyFilter(apps);
            StatusText = $"{apps.Count} applications found.";
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            StatusText = $"The application list could not be read: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Asks winget which apps have a newer version. Nothing is installed here.</summary>
    public async Task CheckUpdatesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Checking for app updates...";
        using var cancellation = new CancellationTokenSource();
        _updatesCancellation = cancellation;
        SetCanCancel(true);
        try
        {
            var (updates, error) = await _updater.ListAsync(cancellation.Token);

            Updates.Clear();
            foreach (var update in updates)
            {
                Updates.Add(new UpdateRow(update));
            }

            _hasCheckedForUpdates = error is null;
            _lastCheckedAt = error is null ? DateTimeOffset.UtcNow : null;
            OnPropertyChanged(nameof(LastCheckedText));

            StatusText = error
                ?? (updates.Count == 0
                    ? "No app updates found."
                    : $"{updates.Count} app update(s) available.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "The update check was stopped.";
        }
        finally
        {
            _updatesCancellation = null;
            SetCanCancel(false);
            IsBusy = false;
        }
    }

    /// <summary>Stops a running update check or batch update.</summary>
    public void CancelUpdates() => _updatesCancellation?.Cancel();

    private void SetCanCancel(bool value)
    {
        if (_canCancel != value)
        {
            _canCancel = value;
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    /// <summary>Installs the newer version of one app.</summary>
    public async Task UpdateAsync(UpdateRow row)
    {
        if (IsBusy || !row.CanUpdate)
        {
            return;
        }

        IsBusy = true;
        IsUpdating = true;
        SetRowsInteractive(false);
        row.MarkUpdating();
        StatusText = $"Updating {row.Name}...";
        using var cancellation = new CancellationTokenSource();
        _updatesCancellation = cancellation;
        SetCanCancel(true);
        try
        {
            var result = await _updater.UpdateAsync(row.Update, NewRunId(), cancellation.Token);
            if (result.Outcome == MaintenanceOutcome.Applied)
            {
                MarkUpdated(row);
                StatusText = $"{row.Name} was updated to {row.Available}.";
            }
            else
            {
                row.MarkFailed(result.Message);
                StatusText = $"{row.Name} was not updated: {result.Message}";
            }
        }
        catch (OperationCanceledException)
        {
            row.MarkStopped();
            StatusText = $"{row.Name} was stopped before it finished.";
        }
        finally
        {
            _updatesCancellation = null;
            SetCanCancel(false);
            IsUpdating = false;
            SetRowsInteractive(true);
            IsBusy = false;
        }
    }

    /// <summary>Installs every pending update, one after another, until done or cancelled.</summary>
    public async Task UpdateAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var pending = Updates.Where(u => u.IsPending).ToList();
        if (pending.Count == 0)
        {
            StatusText = "Nothing is waiting to be updated.";
            return;
        }

        IsBusy = true;
        IsUpdating = true;
        SetRowsInteractive(false);
        var runId = NewRunId();
        int updated = 0, failed = 0;
        bool cancelled = false;
        var reasons = new List<string>();
        using var cancellation = new CancellationTokenSource();
        _updatesCancellation = cancellation;
        SetCanCancel(true);
        try
        {
            foreach (var row in pending)
            {
                cancellation.Token.ThrowIfCancellationRequested();

                row.MarkUpdating();
                StatusText = $"Updating {row.Name} ({updated + failed + 1} of {pending.Count})...";

                var result = await _updater.UpdateAsync(row.Update, runId, cancellation.Token);
                if (result.Outcome == MaintenanceOutcome.Applied)
                {
                    MarkUpdated(row);
                    updated++;
                }
                else
                {
                    row.MarkFailed(result.Message);
                    failed++;
                    if (reasons.Count < 3)
                    {
                        reasons.Add($"{row.Name}: {result.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;

            // Only the row that was running is marked stopped. The rows after it stay
            // queued, so the user can retry them.
            foreach (var row in pending.Where(r => r.State == UpdateState.Updating))
            {
                row.MarkStopped();
            }
        }
        finally
        {
            _updatesCancellation = null;
            SetCanCancel(false);
            IsUpdating = false;
            SetRowsInteractive(true);
            IsBusy = false;
        }

        var summary = new List<string> { $"{updated} app(s) updated" };
        if (failed > 0)
        {
            summary.Add($"{failed} failed ({string.Join("; ", reasons)})");
        }

        if (cancelled)
        {
            var notDone = pending.Count(r => r.IsPending);
            summary.Add($"{notDone} not updated");
        }

        StatusText = string.Join(", ", summary) + (failed > 0 ? ". The History page has the details." : ".");
    }

    private static string NewRunId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Turns every row's button off or on, so a click is never silently ignored.</summary>
    private void SetRowsInteractive(bool interactive)
    {
        foreach (var row in Updates)
        {
            row.IsInteractive = interactive;
        }
    }

    /// <summary>Marks a row done and reflects the new version in the installed-app list.</summary>
    private void MarkUpdated(UpdateRow row)
    {
        row.MarkUpdated();
        ApplyUpdatedVersion(row);
        NotifyUpdatesChanged();
    }

    /// <summary>
    /// Replaces the installed-app entry with the version winget just installed, so the list
    /// no longer shows the old version. The entry is matched by name, the only link winget
    /// gives us back.
    /// </summary>
    private void ApplyUpdatedVersion(UpdateRow row)
    {
        var index = _allApps.FindIndex(a => string.Equals(a.Name, row.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        var old = _allApps[index];
        var refreshed = new InstalledApp
        {
            Id = old.Id,
            Name = old.Name,
            Version = row.Available,
            Publisher = old.Publisher,
            InstallLocation = old.InstallLocation,
            UninstallString = old.UninstallString,
            QuietUninstallString = old.QuietUninstallString,
            Source = old.Source,
            SizeBytes = old.SizeBytes,
            InstallDateUtc = old.InstallDateUtc,
            IconKey = old.IconKey,
            PackageFamilyName = old.PackageFamilyName
        };

        _allApps[index] = refreshed;

        var shown = Apps.IndexOf(old);
        if (shown >= 0)
        {
            Apps[shown] = refreshed;
        }

        if (SelectedApp?.Id == old.Id)
        {
            SelectedApp = refreshed;
        }
    }
    private void ApplyFilter(IReadOnlyList<InstalledApp>? source = null)
    {
        var all = source ?? _allApps;
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? all
            : all.Where(a => a.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

        Apps.Clear();
        foreach (var app in filtered.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            Apps.Add(app);
        }

        OnPropertyChanged(nameof(NoAppsMatch));
    }

    /// <summary>Builds the review for the selected app. Nothing is removed here.</summary>
    public Task ReviewSelectedAsync()
    {
        if (SelectedApp is null)
        {
            return Task.CompletedTask;
        }

        var plan = _leftovers.BuildPlan(SelectedApp);

        Leftovers.Clear();
        foreach (var leftover in plan.Leftovers)
        {
            Leftovers.Add(leftover);
        }

        StatusText = plan.Leftovers.Count == 0
            ? $"No leftover folders were found for {SelectedApp.Name}."
            : $"{plan.Leftovers.Count} leftover folders found for {SelectedApp.Name}.";

        return Task.CompletedTask;
    }

    /// <summary>Runs the app's own uninstaller.</summary>
    public async Task<string?> UninstallSelectedAsync(bool preferSilent)
    {
        if (SelectedApp is null)
        {
            return null;
        }

        if (SelectedAppIsStore)
        {
            // Windows owns the removal of a packaged application.
            return OpenWindowsAppSettings();
        }

        IsBusy = true;
        StatusText = $"Running the uninstaller for {SelectedApp.Name}...";
        try
        {
            var service = new UninstallService(_log);
            var runId = Guid.NewGuid().ToString("N")[..8];
            var outcome = await Task.Run(() => service.RunUninstaller(SelectedApp, preferSilent, runId));

            StatusText = outcome.Message;
            return outcome.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens the Windows page that lists installed applications.</summary>
    public string OpenWindowsAppSettings()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:appsfeatures",
                UseShellExecute = true
            });

            StatusText = "Windows opened the application list. Remove the app there, then review the leftovers here.";
            return StatusText;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText = $"Windows could not open the application list: {e.Message}";
            return StatusText;
        }
    }

    /// <summary>Removes the leftover folders the user selected in the review.</summary>
    public async Task<string?> RemoveSelectedLeftoversAsync()
    {
        if (SelectedApp is null)
        {
            return null;
        }

        var selected = Leftovers.Where(l => l.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = "No leftover folders are selected.";
            return null;
        }

        IsBusy = true;
        StatusText = $"Removing {selected.Count} leftover folders...";
        try
        {
            var service = new UninstallService(_log);
            var runId = Guid.NewGuid().ToString("N")[..8];
            int removed = 0;
            int skipped = 0;
            long bytes = 0;
            var failures = new List<string>();

            var removedItems = new List<LeftoverItem>();
            foreach (var leftover in selected)
            {
                // The protection check, safety guard and logging all live in Core.
                var result = await Task.Run(() => service.RemoveLeftover(leftover.Path, leftover.SizeBytes, _settings, runId));
                if (result.Succeeded)
                {
                    removed++;
                    bytes += result.BytesReclaimed;
                    removedItems.Add(leftover);
                }
                else if (result.FailureReason == UninstallService.ProtectedMessage)
                {
                    skipped++;
                }
                else
                {
                    failures.Add($"{leftover.Name}: {result.FailureReason}");
                }
            }

            // Only drop the folders that actually left the disk. A protected or failed folder
            // stays in the review, so the list still reflects what is on the machine.
            foreach (var leftover in removedItems)
            {
                Leftovers.Remove(leftover);
            }

            var summary = new List<string> { $"Removed {removed} folders, reclaiming {ByteSizeFormatter.Format(bytes)}" };
            if (skipped > 0)
            {
                summary.Add($"{skipped} left in place because they are protected (change that under Settings > Protected paths)");
            }
            if (failures.Count > 0)
            {
                summary.Add($"{failures.Count} could not be removed: {string.Join("; ", failures.Take(2))}" +
                            (failures.Count > 2 ? $" and {failures.Count - 2} more" : string.Empty));
            }

            StatusText = string.Join(". ", summary) + ".";

            return StatusText;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
