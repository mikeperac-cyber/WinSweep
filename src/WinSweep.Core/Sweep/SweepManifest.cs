using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinSweep.Core.Sweep;

public enum SweepRisk { Safe, Caution, Locked }

public enum SweepEntryStatus { Planned, Quarantined, Skipped, Failed, Restored }

/// <summary>One thing a sweep wants to remove, as the caller knows it.</summary>
public sealed record SweepTarget(string Path, string Name, string Category, long SizeBytes);

/// <summary>Outcome of the optional shadow-copy snapshot taken before a sweep.</summary>
public sealed record SnapshotInfo(bool Attempted, bool Succeeded, string Message);

/// <summary>One swept target: what it is, how risky it looks and, after a real sweep, where its archive is.</summary>
public sealed class SweepEntry
{
    /// <summary>The PathGuard.Normalize'd path.</summary>
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    public SweepRisk Risk { get; init; }
    public string RiskReason { get; init; } = string.Empty;

    // Filled in by the vault during a real sweep:
    public SweepEntryStatus Status { get; set; } = SweepEntryStatus.Planned;

    /// <summary>File name inside the sweep folder, e.g. "0003.wsq".</summary>
    public string? Archive { get; set; }

    /// <summary>Lowercase hex SHA-256 of the .wsq file.</summary>
    public string? Sha256 { get; set; }
    public string? AclSddl { get; set; }
    public int FilesArchived { get; set; }
    public List<string> SkippedFiles { get; set; } = new();
    public string Detail { get; set; } = string.Empty;
}

/// <summary>The plan of a sweep, and once the vault has run it, the index used to restore it.</summary>
public sealed class SweepManifest
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public required string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public bool DryRun { get; set; }

    /// <summary>"clean" or "purge", the OperationLog command names.</summary>
    public required string Source { get; init; }
    public long TotalBytes { get; init; }
    public Dictionary<string, long> BytesByCategory { get; init; } = new();
    public List<SweepEntry> Entries { get; init; } = new();

    /// <summary>Always empty: WinSweep never edits the registry.</summary>
    public List<string> RegistryKeys { get; init; } = new();
    public SnapshotInfo? Snapshot { get; set; }

    /// <summary>Writes the manifest as indented JSON, creating the directory if needed.</summary>
    public void Save(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write-then-move so a crash mid-save never leaves a half-written restoration index.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, SerializerOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads a manifest, or null when the file is missing or unreadable.</summary>
    public static SweepManifest? Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SweepManifest>(File.ReadAllText(path), SerializerOptions)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
