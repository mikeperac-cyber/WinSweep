namespace WinSweep.Core.Models;

/// <summary>One item in the uninstall review: a leftover that may belong to an app.</summary>
public sealed class LeftoverItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public long SizeBytes { get; init; }
    public ScanStatus SizeStatus { get; init; } = ScanStatus.Unavailable;

    /// <summary>Why this item is offered, in plain words shown to the user.</summary>
    public required string Reason { get; init; }

    /// <summary>True when the item holds personal data and is never selected by default.</summary>
    public bool HoldsPersonalData { get; init; }

    public bool IsSelected { get; set; }

    public string SizeDisplay => SizeStatus == ScanStatus.Unavailable
        ? "Unknown"
        : ByteSizeFormatter.Format(SizeBytes);
}

/// <summary>The uninstall plan shown to the user before anything is removed.</summary>
public sealed class UninstallPlan
{
    public required InstalledApp App { get; init; }
    public IReadOnlyList<LeftoverItem> Leftovers { get; init; } = Array.Empty<LeftoverItem>();
    public string UninstallCommand { get; init; } = string.Empty;
    public string? Warning { get; init; }

    public long EstimatedBytes => Leftovers.Sum(l => l.SizeBytes);
}
