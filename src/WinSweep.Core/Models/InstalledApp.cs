namespace WinSweep.Core.Models;

/// <summary>An installed application discovered on the machine.</summary>
public sealed class InstalledApp
{
    /// <summary>Stable key used for selection, e.g. <c>registry:{GUID}</c> or <c>package:{fullname}</c>.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string InstallLocation { get; init; } = string.Empty;
    public string UninstallString { get; init; } = string.Empty;
    public string QuietUninstallString { get; init; } = string.Empty;
    public InstallSource Source { get; init; } = InstallSource.Registry;

    /// <summary>Measured size in bytes, or null when it was never measured.</summary>
    public long? SizeBytes { get; init; }
    public DateTime? InstallDateUtc { get; init; }
    public string IconKey { get; init; } = string.Empty;

    public string SourceDisplay => Source switch
    {
        InstallSource.WindowsStore => "Store",
        _ => "Desktop"
    };

    /// <summary>For a Microsoft Store application, the family name that owns its data folder.</summary>
    public string PackageFamilyName { get; init; } = string.Empty;

    public string SizeDisplay => SizeBytes is null
        ? "Unknown"
        : ByteSizeFormatter.Format(SizeBytes.Value);
}

public enum InstallSource
{
    Registry,
    WindowsStore
}
