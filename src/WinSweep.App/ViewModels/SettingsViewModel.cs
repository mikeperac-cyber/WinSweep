using System.Collections.ObjectModel;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;
using WinSweep.Core.Settings;

namespace WinSweep.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private string _statusText = string.Empty;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        ProtectedPaths = new ObservableCollection<string>(settings.ProtectedPaths);
    }

    public ObservableCollection<string> ProtectedPaths { get; }

    public IReadOnlyList<string> SystemProtectedLocations => PathGuard.GetProtectedLocations();

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool UseRecycleBin
    {
        get => _settings.UseRecycleBin;
        set
        {
            if (_settings.UseRecycleBin != value)
            {
                _settings.UseRecycleBin = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool UseQuarantineVault
    {
        get => _settings.UseQuarantineVault;
        set
        {
            if (_settings.UseQuarantineVault != value)
            {
                _settings.UseQuarantineVault = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool SnapshotBeforeSweep
    {
        get => _settings.SnapshotBeforeSweep;
        set
        {
            if (_settings.SnapshotBeforeSweep != value)
            {
                _settings.SnapshotBeforeSweep = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>A double because NumberBox.Value is one; always stored as a whole number of days from 1 to 90.</summary>
    public double VaultRetentionDays
    {
        get => _settings.VaultRetentionDays;
        set
        {
            // A cleared NumberBox reports NaN; keep the old value rather than saving nonsense.
            var days = double.IsNaN(value) ? _settings.VaultRetentionDays : (int)Math.Clamp(Math.Round(value), 1, 90);
            if (_settings.VaultRetentionDays != days)
            {
                _settings.VaultRetentionDays = days;
                _settings.Save();
            }

            OnPropertyChanged();
        }
    }

    public bool MeasureAppSizes
    {
        get => _settings.MeasureAppSizes;
        set
        {
            if (_settings.MeasureAppSizes != value)
            {
                _settings.MeasureAppSizes = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public void AddProtectedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalized = PathGuard.Normalize(path.Trim());
        if (ProtectedPaths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            StatusText = "That path is already protected.";
            return;
        }

        ProtectedPaths.Add(normalized);
        _settings.ProtectedPaths = ProtectedPaths.ToList();
        _settings.Save();
        StatusText = $"{normalized} is now protected.";
    }

    public void RemoveProtectedPath(string path)
    {
        if (ProtectedPaths.Remove(path))
        {
            _settings.ProtectedPaths = ProtectedPaths.ToList();
            _settings.Save();
            StatusText = $"{path} is no longer protected.";
        }
    }
}
