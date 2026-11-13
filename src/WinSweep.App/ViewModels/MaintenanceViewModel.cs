using System.Collections.ObjectModel;
using WinSweep.Core.Logging;
using WinSweep.Core.Maintenance;

namespace WinSweep.App.ViewModels;

/// <summary>One maintenance task with the state of its last run.</summary>
public sealed class MaintenanceTaskItem : ObservableObject
{
    private string _statusText = "Not run yet";
    private MaintenanceOutcome? _lastOutcome;

    public MaintenanceTaskItem(MaintenanceTask task)
    {
        Task = task;
    }

    public MaintenanceTask Task { get; }

    public string Id => Task.Id;
    public string Name => Task.Name;
    public string Description => Task.Description;
    public string Impact => Task.Impact;
    public bool RequiresElevation => Task.RequiresElevation;
    public bool IsCheck => Task.IsCheck;
    public bool StoreAvailable => Task.StoreAvailable;

    /// <summary>True when the task can be started from this installation.</summary>
    public bool CanRun => StoreAvailable || !WinSweep.Core.Platform.AppEnvironment.IsStoreBuild;

    /// <summary>True when the "Run as administrator" button should be offered.</summary>
    public bool CanRunElevated => RequiresElevation && CanRun;

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public MaintenanceOutcome? LastOutcome
    {
        get => _lastOutcome;
        set
        {
            if (SetProperty(ref _lastOutcome, value))
            {
                OnPropertyChanged(nameof(OutcomeText));
                OnPropertyChanged(nameof(OutcomeBrushKey));
            }
        }
    }

    public string OutcomeText => LastOutcome switch
    {
        MaintenanceOutcome.Applied => "Done",
        MaintenanceOutcome.Unchanged => "Nothing to do",
        MaintenanceOutcome.Skipped => "Skipped",
        MaintenanceOutcome.Unavailable => "Not available",
        MaintenanceOutcome.Failed => "Failed",
        _ => string.Empty
    };

    public string OutcomeBrushKey => LastOutcome switch
    {
        MaintenanceOutcome.Applied => "PositiveBrush",
        MaintenanceOutcome.Failed => "CriticalBrush",
        MaintenanceOutcome.Skipped => "WarningBrush",
        _ => "SubtleTextBrush"
    };
}

public sealed class MaintenanceViewModel : ObservableObject
{
    private readonly OperationLog _log;
    private readonly MaintenanceService _service;
    private bool _isBusy;
    private bool _hasStarted;
    private string _statusText = "Every task states what it changes before it runs.";
    private MaintenanceTaskItem? _runningTask;

    public MaintenanceViewModel(OperationLog log)
    {
        _log = log;
        _service = new MaintenanceService(log, () => WinSweep.Core.Cleanup.CleanupScanner.IsElevated());

        Tasks = new ObservableCollection<MaintenanceTaskItem>(
            MaintenanceCatalog.GetTasks().Select(t => new MaintenanceTaskItem(t)));
    }

    public ObservableCollection<MaintenanceTaskItem> Tasks { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(LandingVisible));
                OnPropertyChanged(nameof(TasksVisible));
            }
        }
    }

    public bool HasStarted
    {
        get => _hasStarted;
        private set
        {
            if (SetProperty(ref _hasStarted, value))
            {
                OnPropertyChanged(nameof(LandingVisible));
                OnPropertyChanged(nameof(TasksVisible));
            }
        }
    }

    public bool LandingVisible => !HasStarted && !IsBusy;
    public bool TasksVisible => HasStarted && !IsBusy;

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public MaintenanceTaskItem? RunningTask
    {
        get => _runningTask;
        private set => SetProperty(ref _runningTask, value);
    }

    /// <summary>True when this process already runs with administrator rights.</summary>
    public bool IsElevated => WinSweep.Core.Cleanup.CleanupScanner.IsElevated();

    /// <summary>True when this process has a package identity, as a Microsoft Store package does.</summary>
    public bool IsStoreBuild => WinSweep.Core.Platform.AppEnvironment.IsStoreBuild;

    public void ShowTasks() => HasStarted = true;

    /// <summary>Runs one task and reports what happened.</summary>
    public async Task<MaintenanceTaskItem?> RunAsync(MaintenanceTaskItem item)
    {
        if (IsBusy)
        {
            return null;
        }

        IsBusy = true;
        HasStarted = true;
        RunningTask = item;
        item.StatusText = "Running...";

        try
        {
            var runId = Guid.NewGuid().ToString("N")[..8];
            var result = await Task.Run(() => _service.RunAsync(item.Id, runId));

            item.LastOutcome = result.Outcome;
            item.StatusText = result.Message;

            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                item.StatusText = $"{result.Message} — {result.Output}";
            }

            StatusText = $"{item.Name}: {result.Message}";
            return item;
        }
        finally
        {
            IsBusy = false;
            RunningTask = null;
        }
    }

    /// <summary>
    /// Starts this app again with administrator rights and the task id, so the task
    /// runs in a process that can actually perform it.
    /// </summary>
    public static bool RelaunchElevated(string taskId)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"--page Maintenance --maintenance {taskId}",
                UseShellExecute = true,
                Verb = "runas"
            };

            System.Diagnostics.Process.Start(startInfo);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
