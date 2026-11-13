using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinSweep.Core.Settings;

/// <summary>User preferences. Stored next to the app data, never in the registry.</summary>
public sealed class AppSettings
{
    /// <summary>Paths the user protected. A matching path is never removed.</summary>
    public List<string> ProtectedPaths { get; set; } = new();

    /// <summary>Remove to the Recycle Bin instead of deleting permanently.</summary>
    public bool UseRecycleBin { get; set; } = true;

    /// <summary>Move swept items to the encrypted quarantine vault so a sweep can be undone. Takes precedence over the Recycle Bin.</summary>
    public bool UseQuarantineVault { get; set; } = true;

    /// <summary>Days a quarantined sweep is kept before it is deleted for good.</summary>
    public int VaultRetentionDays { get; set; } = 7;

    /// <summary>Try to create a Windows shadow copy before a sweep, when running as administrator.</summary>
    public bool SnapshotBeforeSweep { get; set; } = true;

    /// <summary>Minimum age in days before a cache target becomes eligible.</summary>
    public int MinimumCacheAgeDays { get; set; } = 1;

    /// <summary>Include targets that belong to applications that are currently running.</summary>
    public bool IncludeBusyAppCaches { get; set; }

    /// <summary>Scan folder sizes for installed apps. Slow on large installations.</summary>
    public bool MeasureAppSizes { get; set; } = true;

    /// <summary>Project folders the user explicitly asked WinSweep to inspect for build artifacts.</summary>
    public List<string> ProjectScanRoots { get; set; } = new();

    /// <summary>True after the user has saved their project scan-root choice, including an empty list.</summary>
    public bool ProjectScanRootsConfigured { get; set; }

    /// <summary>Days without a commit or source edit before a project's build output becomes eligible.</summary>
    public int ProjectInactivityDays { get; set; } = 30;

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinSweep",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to defaults; a damaged settings file must not block the app.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"WinSweep: could not save settings: {e.Message}");
        }
    }

    /// <summary>True when the user protected this path, directly or through a parent folder.</summary>
    public bool IsProtected(string path)
    {
        var normalized = Safety.PathGuard.Normalize(path);
        foreach (var entry in ProtectedPaths)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            try
            {
                if (Safety.PathGuard.IsSameOrUnder(normalized, entry))
                {
                    return true;
                }
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException)
            {
                // Ignore an entry that no longer resolves.
            }
        }

        return false;
    }
}
