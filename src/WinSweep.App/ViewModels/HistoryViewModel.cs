using System.Collections.ObjectModel;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;

namespace WinSweep.App.ViewModels;

public sealed class HistoryViewModel : ObservableObject
{
    private readonly OperationLog _log;
    private string _statusText = string.Empty;

    public HistoryViewModel(OperationLog log)
    {
        _log = log;
        Records = new ObservableCollection<OperationRecord>();
    }

    public ObservableCollection<OperationRecord> Records { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void Refresh()
    {
        Records.Clear();
        foreach (var record in _log.Read(500))
        {
            Records.Add(record);
        }

        StatusText = Records.Count == 0
            ? "No actions have been recorded yet."
            : $"{Records.Count} actions recorded.";
    }
}
