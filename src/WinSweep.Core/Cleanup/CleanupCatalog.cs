using WinSweep.Core.Models;

namespace WinSweep.Core.Cleanup;

/// <summary>One cleanup rule: a known location and the reason it is disposable.</summary>
public sealed class CleanupRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }

    /// <summary>Path template. <c>%VAR%</c> is expanded and a single <c>*</c> wildcards one level.</summary>
    public required string PathTemplate { get; init; }

    public CleanupRisk Risk { get; init; } = CleanupRisk.Rebuildable;

    /// <summary>Minimum age in days before the target may be removed.</summary>
    public int MinimumAgeDays { get; init; }

    /// <summary>Processes that must not be running, otherwise the target is kept.</summary>
    public IReadOnlyList<string> BlockingProcesses { get; init; } = Array.Empty<string>();

    /// <summary>True when the location belongs to the machine rather than the user.</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>False keeps the rule out of the default scan but available in settings.</summary>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>True when the template matches files rather than folders.</summary>
    public bool TargetsFiles { get; init; }
}

/// <summary>
/// The catalog of known disposable Windows locations. Every entry names the reason it
/// is safe, so the review screen can explain itself without a database.
/// </summary>
public static class CleanupCatalog
{
    public static IReadOnlyList<CleanupRule> GetRules() => Rules;

    private static readonly CleanupRule[] Rules =
    {
        new()
        {
            Id = "user-temp",
            Name = "Temporary files",
            Category = "System",
            Description = "Files the system and apps left in your user temp folder.",
            PathTemplate = "%LOCALAPPDATA%\\Temp",
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "crash-dumps",
            Name = "Application crash dumps",
            Category = "System",
            Description = "Diagnostic memory dumps from programs that crashed.",
            PathTemplate = "%LOCALAPPDATA%\\CrashDumps",
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "windows-inet-cache",
            Name = "Internet cache",
            Category = "System",
            Description = "Cached web content that the system rebuilds on demand.",
            PathTemplate = "%LOCALAPPDATA%\\Microsoft\\Windows\\INetCache",
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "windows-wer",
            Name = "Windows error reports",
            Category = "System",
            Description = "Error reports the system queued for Microsoft.",
            PathTemplate = "%LOCALAPPDATA%\\Microsoft\\Windows\\WER",
            MinimumAgeDays = 7
        },
        new()
        {
            Id = "thumbnails",
            Name = "Thumbnail cache",
            Category = "System",
            Description = "Preview images for folders and files. Explorer rebuilds them.",
            PathTemplate = "%LOCALAPPDATA%\\Microsoft\\Windows\\Explorer\\thumbcache_*.db",
            TargetsFiles = true,
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "directx-shaders",
            Name = "DirectX shader cache",
            Category = "Graphics",
            Description = "Compiled shaders that games rebuild on their next launch.",
            PathTemplate = "%LOCALAPPDATA%\\D3DSCache",
            MinimumAgeDays = 7
        },
        new()
        {
            Id = "nvidia-shaders",
            Name = "NVIDIA shader cache",
            Category = "Graphics",
            Description = "Compiled shaders that the driver rebuilds on demand.",
            PathTemplate = "%LOCALAPPDATA%\\NVIDIA\\DXCache",
            MinimumAgeDays = 7
        },
        new()
        {
            Id = "amd-shaders",
            Name = "AMD shader cache",
            Category = "Graphics",
            Description = "Compiled shaders that the AMD driver rebuilds on demand.",
            PathTemplate = "%LOCALAPPDATA%\\AMD\\DxCache",
            MinimumAgeDays = 7
        },
        new()
        {
            Id = "nvidia-gl-cache",
            Name = "NVIDIA OpenGL cache",
            Category = "Graphics",
            Description = "Compiled OpenGL shaders that the driver rebuilds on demand.",
            PathTemplate = "%LOCALAPPDATA%\\NVIDIA\\GLCache",
            MinimumAgeDays = 7
        },
        new()
        {
            Id = "chrome-cache",
            Name = "Google Chrome cache",
            Category = "Browsers",
            Description = "Cached pages, code and GPU data that Chrome rebuilds on demand.",
            PathTemplate = "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\Cache",
            BlockingProcesses = new[] { "chrome" },
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "chrome-code-cache",
            Name = "Google Chrome code cache",
            Category = "Browsers",
            Description = "Cached scripts that Chrome rebuilds on demand.",
            PathTemplate = "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\Code Cache",
            BlockingProcesses = new[] { "chrome" },
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "chrome-gpu-cache",
            Name = "Google Chrome GPU cache",
            Category = "Browsers",
            Description = "Cached graphics data that Chrome rebuilds on demand.",
            PathTemplate = "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\*\\GPUCache",
            BlockingProcesses = new[] { "chrome" },
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "edge-cache",
            Name = "Microsoft Edge cache",
            Category = "Browsers",
            Description = "Cached pages, code and GPU data that Edge rebuilds on demand.",
            PathTemplate = "%LOCALAPPDATA%\\Microsoft\\Edge\\User Data\\*\\Cache",
            BlockingProcesses = new[] { "msedge" },
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "edge-code-cache",
            Name = "Microsoft Edge code cache",
            Category = "Browsers",
            Description = "Cached scripts that Edge rebuilds on demand.",
            PathTemplate = "%LOCALAPPDATA%\\Microsoft\\Edge\\User Data\\*\\Code Cache",
            BlockingProcesses = new[] { "msedge" },
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "edge-gpu-cache",
            Name = "Microsoft Edge GPU cache",
            Category = "Browsers",
            Description = "Cached graphics data that Edge rebuilds on demand.",
            PathTemplate = "%LOCALAPPDATA%\\Microsoft\\Edge\\User Data\\*\\GPUCache",
            BlockingProcesses = new[] { "msedge" },
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "firefox-cache",
            Name = "Firefox cache",
            Category = "Browsers",
            Description = "Cached pages that Firefox rebuilds on demand.",
            PathTemplate = "%LOCALAPPDATA%\\Mozilla\\Firefox\\Profiles\\*\\cache2",
            BlockingProcesses = new[] { "firefox" },
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "vscode-cache",
            Name = "Visual Studio Code cache",
            Category = "Developer tools",
            Description = "Extension and workspace caches that VS Code rebuilds.",
            PathTemplate = "%APPDATA%\\Code\\Cache",
            BlockingProcesses = new[] { "Code" },
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "vscode-cached-data",
            Name = "Visual Studio Code cached data",
            Category = "Developer tools",
            Description = "Cached extension data that VS Code rebuilds.",
            PathTemplate = "%APPDATA%\\Code\\CachedData",
            BlockingProcesses = new[] { "Code" },
            MinimumAgeDays = 1
        },
        new()
        {
            Id = "npm-cache",
            Name = "npm cache",
            Category = "Developer tools",
            Description = "Downloaded npm packages. The next install downloads them again.",
            PathTemplate = "%LOCALAPPDATA%\\npm-cache",
            BlockingProcesses = new[] { "node" },
            MinimumAgeDays = 7
        },
        new()
        {
            Id = "pip-cache",
            Name = "pip cache",
            Category = "Developer tools",
            Description = "Downloaded Python packages. The next install downloads them again.",
            PathTemplate = "%LOCALAPPDATA%\\pip\\Cache",
            BlockingProcesses = new[] { "python" },
            MinimumAgeDays = 7
        },
        new()
        {
            Id = "nuget-packages",
            Name = "NuGet package cache",
            Category = "Developer tools",
            Description = "Restored NuGet packages. A later build needs them again, so they are only offered for review.",
            PathTemplate = "%USERPROFILE%\\.nuget\\packages",
            Risk = CleanupRisk.Review,
            MinimumAgeDays = 30
        },
        new()
        {
            Id = "windows-temp",
            Name = "Windows temp folder",
            Category = "System",
            Description = "Temporary files owned by Windows. Needs an administrator account.",
            PathTemplate = "%WINDIR%\\Temp",
            RequiresElevation = true,
            EnabledByDefault = false,
            MinimumAgeDays = 7
        }
    };
}
