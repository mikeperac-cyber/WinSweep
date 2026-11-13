using System.ComponentModel;

namespace WinSweep.Core.Models;

/// <summary>A generated project folder that can be rebuilt from the project's source.</summary>
public sealed class ProjectArtifact : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Description { get; init; }
    public long SizeBytes { get; init; }
    public ScanStatus SizeStatus { get; init; } = ScanStatus.Unavailable;
    public DateTime? LastActivityUtc { get; init; }

    /// <summary>When the owning project was last worked on (Git commit or source edit), if known.</summary>
    public DateTime? ProjectLastActivityUtc { get; init; }
    public string? BlockedReason { get; init; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string SizeDisplay => SizeStatus == ScanStatus.Unavailable
        ? "Unknown"
        : SizeStatus == ScanStatus.Partial
            ? $"≥ {ByteSizeFormatter.Format(SizeBytes)}"
            : ByteSizeFormatter.Format(SizeBytes);

    public string ActivityDisplay => ProjectLastActivityUtc is not null
        ? $"Project last worked on {ProjectLastActivityUtc.Value.ToLocalTime():d MMM yyyy}"
        : LastActivityUtc is null
            ? "Activity unknown"
            : $"Last changed {LastActivityUtc.Value.ToLocalTime():d MMM yyyy}";

    public string StateText => BlockedReason ?? Description;
}

public sealed record ProjectArtifactScanProgress(
    int ScannedDirectories,
    int FoundArtifacts,
    string CurrentPath,
    string Message);

public sealed class ProjectArtifactScanResult
{
    public IReadOnlyList<ProjectArtifact> Artifacts { get; init; } = Array.Empty<ProjectArtifact>();
    public IReadOnlyList<string> UnavailableRoots { get; init; } = Array.Empty<string>();
    public int ScannedDirectories { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsPartial { get; init; }
    public bool TimedOut { get; init; }
}
