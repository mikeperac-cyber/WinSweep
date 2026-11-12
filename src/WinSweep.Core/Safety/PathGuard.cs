using WinSweep.Core.Models;

namespace WinSweep.Core.Safety;

/// <summary>
/// The single funnel every deletion passes through. Nothing in the app removes a path
/// without asking this type first, so the protection rules live in one place.
/// </summary>
public static class PathGuard
{
    /// <summary>Filesystem roots that are never removable.</summary>
    private static readonly string[] ProtectedRoots =
    {
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData",
        @"C:\Users\Default",
        @"C:\Recovery",
        @"C:\$Recycle.Bin",
        @"C:\System Volume Information"
    };

    /// <summary>
    /// Top-level system folders that are never removable. These are only matched directly
    /// below a drive root, so a same-named folder inside a user profile stays usable.
    /// </summary>
    private static readonly string[] ProtectedSystemFolders =
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "$Recycle.Bin",
        "System Volume Information",
        "Boot",
        "EFI",
        "Recovery"
    };

    /// <summary>Extensions that hold application state rather than disposable cache.</summary>
    private static readonly string[] PersonalDataExtensions =
    {
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".csv",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".heic", ".mp4", ".mov", ".mp3", ".wav",
        ".zip", ".7z", ".rar", ".psd", ".ai", ".dwg", ".vsdx", ".msg", ".eml", ".pst",
        ".vhd", ".vhdx", ".vmdk", ".vdi", ".key", ".pem", ".pfx", ".p12"
    };

    /// <summary>Names of folders that hold user documents even without an extension.</summary>
    private static readonly string[] PersonalFolderNames =
    {
        "Documents", "Desktop", "Pictures", "Videos", "Music", "Downloads", "Music",
        "OneDrive", "Dropbox", "iCloudDrive"
    };

    /// <summary>Normalizes a path for comparison: full path, trimmed, no trailing separator.</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    /// <summary>True when the path is a filesystem root such as C:\ or a UNC share root.</summary>
    public static bool IsRoot(string path)
    {
        var full = Normalize(path);
        var root = Path.GetPathRoot(full);
        return root is not null && string.Equals(root.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the path is inside one of the protected system locations.</summary>
    public static bool IsProtectedLocation(string path)
    {
        var full = Normalize(path);

        foreach (var root in ProtectedRoots)
        {
            if (IsSameOrUnder(full, root))
            {
                return true;
            }
        }

        if (IsRoot(full))
        {
            return true;
        }

        // A user's own profile root is protected; its contents are not.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) &&
            string.Equals(full, Normalize(profile), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // A top-level system folder is protected as a whole.
        if (segments.Length >= 2 && ProtectedSystemFolders.Contains(segments[1], StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // The shared "Users" container is protected, but a profile inside it is not.
        if (segments.Length == 2 && string.Equals(segments[1], "Users", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>True when the path holds personal user data and must not be auto-selected.</summary>
    public static bool LooksLikePersonalData(string path)
    {
        var full = Normalize(path);
        var extension = Path.GetExtension(full);
        if (!string.IsNullOrEmpty(extension) &&
            PersonalDataExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => PersonalFolderNames.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>True when the path is a reparse point such as a junction or a symbolic link.</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when any existing path component from the volume root to this path is a
    /// junction or symbolic link. This prevents a safe-looking child path from being
    /// redirected through a linked parent.
    /// </summary>
    public static bool HasReparsePointInPath(string path)
    {
        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return true;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return true;
        }

        var current = root;
        if (IsExistingReparsePoint(current))
        {
            return true;
        }

        var remainder = full[root.Length..];
        foreach (var segment in remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsExistingReparsePoint(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExistingReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>True when <paramref name="candidate"/> is the same as, or below, <paramref name="root"/>.</summary>
    public static bool IsSameOrUnder(string candidate, string root)
    {
        var c = Normalize(candidate);
        var r = Normalize(root);
        if (string.Equals(c, r, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The decision returned by the guard for one removal request.
    /// </summary>
    public readonly record struct Verdict(bool Allowed, string Reason)
    {
        public static Verdict Allow { get; } = new(true, string.Empty);
        public static Verdict Reject(string reason) => new(false, reason);
    }

    /// <summary>
    /// Decides whether a path may be removed. This is the only place that answers that question.
    /// </summary>
    public static Verdict Evaluate(string path, bool allowProtectedLocation = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Verdict.Reject("Empty path");
        }

        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return Verdict.Reject("Path could not be resolved");
        }

        if (IsRoot(full))
        {
            return Verdict.Reject("Filesystem root");
        }

        if (!allowProtectedLocation && IsProtectedLocation(full))
        {
            return Verdict.Reject("Protected system location");
        }

        if (HasReparsePointInPath(full))
        {
            return Verdict.Reject("Path crosses a junction or symbolic link");
        }

        return Verdict.Allow;
    }

    /// <summary>Lists the protected locations, used by the settings screen to explain the rules.</summary>
    public static IReadOnlyList<string> GetProtectedLocations() => ProtectedRoots;
}
