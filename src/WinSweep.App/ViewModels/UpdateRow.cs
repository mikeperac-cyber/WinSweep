using WinSweep.Core.Apps;

namespace WinSweep.App.ViewModels;

/// <summary>What is happening to one listed update, so the row can show its own state.</summary>
public enum UpdateState
{
    Queued,
    Updating,
    Updated,
    Failed,
    Stopped
}

/// <summary>
/// One row in the updates list. An <see cref="AppUpdate"/> is immutable, but a row needs
/// to report progress and a per-app failure, so it carries that state alongside it.
/// </summary>
public sealed class UpdateRow : ObservableObject
{
    private UpdateState _state = UpdateState.Queued;
    private string _detail = string.Empty;
    private bool _isInteractive = true;

    public UpdateRow(AppUpdate update)
    {
        Update = update;
    }

    public AppUpdate Update { get; }

    public string Name => Update.Name;
    public string Id => Update.Id;
    public string Version => Update.Version;
    public string Available => Update.Available;

    public UpdateState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CanUpdate));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(HasStateText));
            }
        }
    }

    /// <summary>
    /// False while any update is running, so every row's button is disabled rather than
    /// silently ignoring the click. The view model owns this; a row never sets it alone.
    /// </summary>
    public bool IsInteractive
    {
        get => _isInteractive;
        set
        {
            if (SetProperty(ref _isInteractive, value))
            {
                OnPropertyChanged(nameof(CanUpdate));
            }
        }
    }

    /// <summary>The tool's own reason when the update failed, otherwise empty.</summary>
    public string Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(HasStateText));
            }
        }
    }

    /// <summary>True while this row's update is running, so the UI can show a ring.</summary>
    public bool IsRunning => State == UpdateState.Updating;

    /// <summary>True only when the row can be started right now.</summary>
    public bool CanUpdate => IsInteractive && State is UpdateState.Queued or UpdateState.Failed or UpdateState.Stopped;

    /// <summary>True while the row still needs work.</summary>
    public bool IsPending => State is UpdateState.Queued or UpdateState.Failed or UpdateState.Stopped;

    /// <summary>True when the row has a status worth showing.</summary>
    public bool HasStateText => StateText.Length > 0;

    public string StateText => State switch
    {
        UpdateState.Updating => "Updating…",
        UpdateState.Updated => "Updated",
        UpdateState.Failed => string.IsNullOrWhiteSpace(Detail) ? "Could not update" : $"Could not update: {Detail}",
        UpdateState.Stopped => "Stopped",
        _ => string.Empty
    };

    public void MarkQueued()
    {
        Detail = string.Empty;
        State = UpdateState.Queued;
    }

    public void MarkUpdating()
    {
        Detail = string.Empty;
        State = UpdateState.Updating;
    }

    public void MarkUpdated() => State = UpdateState.Updated;

    public void MarkFailed(string? detail)
    {
        Detail = detail ?? string.Empty;
        State = UpdateState.Failed;
    }

    public void MarkStopped() => State = UpdateState.Stopped;
}
