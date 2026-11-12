using System.ComponentModel;

namespace WinSweep.Core.Models;

/// <summary>How risky it is to remove a cleanup candidate.</summary>
public enum CleanupRisk
{
    /// <summary>Rebuildable by the owning application on next launch.</summary>
    Rebuildable,

    /// <summary>Disposable, but it holds user-visible state that should be confirmed.</summary>
    Review,

    /// <summary>Never removed automatically; listed for information only.</summary>
    Protected
}

/// <summary>A cleanup target discovered by the scanner.</summary>
public sealed class CleanupCandidate : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public CleanupRisk Risk { get; init; } = CleanupRisk.Rebuildable;

    /// <summary>Minimum age before the target may be removed, in days. Zero means any age.</summary>
    public int MinimumAgeDays { get; init; }

    public long SizeBytes { get; init; }
    public ScanStatus SizeStatus { get; init; } = ScanStatus.Unavailable;
    public DateTime? LastWriteTimeUtc { get; init; }
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

    /// <summary>Set when the owning application was running and the target was kept.</summary>
    public string? BlockedReason { get; init; }

    /// <summary>Set when the target is safe but not eligible yet, for example because it is too new.</summary>
    public string? DeferredReason { get; init; }

    public bool AgeAllowsRemoval(DateTime nowUtc)
    {
        if (MinimumAgeDays <= 0 || LastWriteTimeUtc is null)
        {
            return true;
        }

        var age = nowUtc - LastWriteTimeUtc.Value;
        return age >= TimeSpan.FromDays(MinimumAgeDays);
    }

    public string SizeDisplay => SizeStatus == ScanStatus.Unavailable
        ? "Unknown"
        : ByteSizeFormatter.Format(SizeBytes);

    /// <summary>Explains why the item is offered, or why it stays untouched.</summary>
    public string StateText => BlockedReason ?? DeferredReason ?? Description;
}
