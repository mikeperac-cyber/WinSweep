using System.Collections.ObjectModel;
using WinSweep.Core.Apps;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Settings;

namespace WinSweep.App.ViewModels;

public sealed class AppsViewModel : ObservableObject
{
    private readonly OperationLog _log;
    private readonly AppSettings _settings;
    private readonly AppInventory _inventory = new();
    private readonly LeftoverScanner _leftovers = new();
    private List<InstalledApp> _allApps = new();
    private bool _isBusy;
    private string _searchText = string.Empty;
    private string _statusText = "Loading installed applications...";
    private InstalledApp? _selectedApp;

    public AppsViewModel(OperationLog log, AppSettings settings)
    {
        _log = log;
        _settings = settings;
        Apps = new ObservableCollection<InstalledApp>();
        Leftovers = new ObservableCollection<LeftoverItem>();
    }

    public ObservableCollection<InstalledApp> Apps { get; }
    public ObservableCollection<LeftoverItem> Leftovers { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
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
            long bytes = 0;
            var failures = new List<string>();

            foreach (var leftover in selected)
            {
                var result = await Task.Run(() => Core.Services.RecycleBin.Send(leftover.Path, leftover.SizeBytes));
                if (result.Succeeded)
                {
                    removed++;
                    bytes += result.BytesReclaimed;
                    _log.Append(new OperationRecord
                    {
                        Command = "uninstall",
                        RunId = runId,
                        Action = OperationAction.Recycled,
                        Path = leftover.Path,
                        Detail = leftover.Name,
                        SizeBytes = result.BytesReclaimed
                    });
                }
                else
                {
                    failures.Add($"{leftover.Name}: {result.FailureReason}");
                    _log.Append(new OperationRecord
                    {
                        Command = "uninstall",
                        RunId = runId,
                        Action = OperationAction.Failed,
                        Path = leftover.Path,
                        Detail = result.FailureReason ?? "Unknown failure"
                    });
                }
            }

            foreach (var leftover in selected)
            {
                Leftovers.Remove(leftover);
            }

            StatusText = failures.Count > 0
                ? $"Removed {removed} folders ({ByteSizeFormatter.Format(bytes)}); {failures.Count} could not be removed."
                : $"Removed {removed} folders, reclaiming {ByteSizeFormatter.Format(bytes)}.";

            return StatusText;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
