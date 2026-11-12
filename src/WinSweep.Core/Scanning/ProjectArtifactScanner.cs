using System.Diagnostics;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;

namespace WinSweep.Core.Scanning;

/// <summary>
/// Finds recognized build output inside user-selected project roots. It does not
/// search the whole disk, cross junctions, enter generated dependency folders, or
/// offer recent, uncertain, tracked, or sensitive content for cleanup.
/// </summary>
public sealed class ProjectArtifactScanner
{
    private static readonly Dictionary<string, string> ArtifactDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node_modules"] = "Installed JavaScript packages. The package manager can restore them from the lockfile.",
        ["target"] = "Rust build output. Cargo rebuilds it from the source.",
        ["build"] = "Build output. The project's build tools can recreate it.",
        ["dist"] = "Compiled distribution output. The project's build tools can recreate it.",
        [".build"] = "Swift Package Manager build output. SwiftPM recreates it from the package sources.",
        [".next"] = "Next.js generated output. Next.js recreates it on the next build.",
        [".turbo"] = "Turborepo's local task cache. Turborepo recreates it.",
        ["out"] = "Generated build output. The project's build tools can recreate it.",
        ["coverage"] = "Generated test coverage reports. The test runner recreates them.",
        ["__pycache__"] = "Compiled Python bytecode. Python recreates it when modules are imported.",
        [".pytest_cache"] = "Pytest's local test cache. Pytest recreates it.",
        [".mypy_cache"] = "Mypy type-checking cache. Mypy recreates it.",
        [".ruff_cache"] = "Ruff's local lint cache. Ruff recreates it.",
        [".venv"] = "A project-local Python environment. It can be recreated from the project's dependency files.",
        ["venv"] = "A project-local Python environment. It can be recreated from the project's dependency files.",
        ["bin"] = "Compiled application output. The project's build tools can recreate it.",
        ["obj"] = "Intermediate .NET build output. MSBuild recreates it."
    };

    private static readonly HashSet<string> ProjectMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "package.json", "Cargo.toml", "go.mod", "pyproject.toml", "requirements.txt",
        "pom.xml", "build.gradle", "build.gradle.kts", "Makefile", "composer.json", "Gemfile",
        "mix.exs", "Package.swift", "CMakeLists.txt"
    };

    private static readonly HashSet<string> SkipDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "AppData", "Windows", "Program Files", "Program Files (x86)",
        "System Volume Information", "$Recycle.Bin", "OneDriveTemp", "node_modules", "target",
        ".venv", "venv", "__pycache__", ".next", ".turbo", "vendor", "Pods", "packages"
    };

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "credentials.json", "secrets.json", "id_rsa", "id_ed25519", "id_ecdsa",
        "id_dsa", "private.key", "client_secret.json"
    };

    private readonly TimeSpan _scanBudget;
    private readonly TimeSpan _artifactBudget;
    private readonly TimeSpan _minimumAge;
    private readonly int _maxDirectories;
    private readonly int _maxDepth;

    public ProjectArtifactScanner(
        TimeSpan? scanBudget = null,
        TimeSpan? artifactBudget = null,
        TimeSpan? minimumAge = null,
        int maxDirectories = 20000,
        int maxDepth = 12)
    {
        _scanBudget = scanBudget ?? TimeSpan.FromSeconds(25);
        _artifactBudget = artifactBudget ?? TimeSpan.FromSeconds(1.5);
        _minimumAge = minimumAge ?? TimeSpan.FromDays(7);
        _maxDirectories = maxDirectories;
        _maxDepth = maxDepth;
    }

    public ProjectArtifactScanResult Scan(
        IEnumerable<string> roots,
        Action<ProjectArtifactScanProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow + _scanBudget;
        var artifacts = new List<ProjectArtifact>();
        var unavailableRoots = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, string? ProjectRoot, int Depth)>();
        var scanned = 0;
        var partial = false;
        var timedOut = false;

        foreach (var rawRoot in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string root;
            try
            {
                root = PathGuard.Normalize(rawRoot);
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
            {
                unavailableRoots.Add(rawRoot);
                partial = true;
                continue;
            }

            if (!Directory.Exists(root) ||
                PathGuard.IsRoot(root) ||
                PathGuard.IsProtectedLocation(root) ||
                PathGuard.HasReparsePointInPath(root))
            {
                unavailableRoots.Add(root);
                partial = true;
                continue;
            }

            pending.Push((root, null, 0));
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow >= deadline || scanned >= _maxDirectories)
            {
                timedOut = true;
                partial = true;
                break;
            }

            var (directory, inheritedProjectRoot, depth) = pending.Pop();
            string fullDirectory;
            try
            {
                fullDirectory = PathGuard.Normalize(directory);
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
            {
                partial = true;
                continue;
            }

            if (!visited.Add(fullDirectory) || PathGuard.IsReparsePoint(fullDirectory))
            {
                continue;
            }

            if (depth > _maxDepth)
            {
                partial = true;
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(fullDirectory);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                unavailableRoots.Add(fullDirectory);
                partial = true;
                continue;
            }

            scanned++;
            var projectRoot = HasProjectMarker(fullDirectory, entries) ? fullDirectory : inheritedProjectRoot;
            var childDirectories = new List<string>();

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var isDirectory = Directory.Exists(entry);

                if (isDirectory && projectRoot is not null &&
                    ArtifactDescriptions.TryGetValue(name, out var description) &&
                    IsArtifactForProject(projectRoot, name))
                {
                    var inspected = InspectArtifact(projectRoot, entry, name, description, cancellationToken);
                    artifacts.Add(inspected);

                    if (inspected.SizeStatus != ScanStatus.Complete || inspected.BlockedReason is not null)
                    {
                        partial |= inspected.SizeStatus != ScanStatus.Complete;
                    }

                    onProgress?.Invoke(new ProjectArtifactScanProgress(
                        scanned,
                        artifacts.Count,
                        entry,
                        $"Found {artifacts.Count} build artifact(s)."));

                    // Never recurse into output/dependency folders. They can contain
                    // thousands of nested packages and are not project roots themselves.
                    continue;
                }

                if (!isDirectory || SkipDirectoryNames.Contains(name) || PathGuard.IsReparsePoint(entry))
                {
                    continue;
                }

                childDirectories.Add(entry);
            }

            // Reverse so the stack visits folder names in stable alphabetical order.
            childDirectories.Sort(StringComparer.OrdinalIgnoreCase);
            for (var i = childDirectories.Count - 1; i >= 0; i--)
            {
                pending.Push((childDirectories[i], projectRoot, depth + 1));
            }

            if (scanned % 100 == 0)
            {
                onProgress?.Invoke(new ProjectArtifactScanProgress(
                    scanned,
                    artifacts.Count,
                    fullDirectory,
                    $"Checked {scanned:N0} folders; found {artifacts.Count} artifact(s)."));
            }
        }

        timer.Stop();
        return new ProjectArtifactScanResult
        {
            Artifacts = artifacts
                .OrderByDescending(a => a.SizeBytes)
                .ThenBy(a => a.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            UnavailableRoots = unavailableRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ScannedDirectories = scanned,
            Duration = timer.Elapsed,
            IsPartial = partial || pending.Count > 0,
            TimedOut = timedOut
        };
    }

    /// <summary>Rechecks a candidate at the removal boundary.</summary>
    public ProjectArtifact Revalidate(ProjectArtifact artifact, CancellationToken cancellationToken = default)
    {
        if (!IsRecognizedArtifact(artifact.Name) || !HasProjectMarker(artifact.ProjectPath))
        {
            return CopyWithBlock(artifact, "The project marker or artifact name changed, so it is kept.");
        }

        return InspectArtifact(artifact.ProjectPath, artifact.Path, artifact.Name, artifact.Description, cancellationToken);
    }

    public static bool IsRecognizedArtifact(string name) => ArtifactDescriptions.ContainsKey(name);

    public static bool HasProjectMarker(string directory)
    {
        if (!Directory.Exists(directory) || PathGuard.HasReparsePointInPath(directory))
        {
            return false;
        }

        try
        {
            return HasProjectMarker(directory, Directory.GetFileSystemEntries(directory));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private ProjectArtifact InspectArtifact(
        string projectRoot,
        string path,
        string name,
        string description,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow + _artifactBudget;
        var pending = new Stack<string>();
        var latest = (DateTime?)null;
        var size = 0L;
        var files = 0L;
        var status = ScanStatus.Complete;
        string? block = null;
        pending.Push(path);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow >= deadline)
            {
                status = ScanStatus.Partial;
                block ??= "The folder could not be fully checked before the scan time limit.";
                break;
            }

            var current = pending.Pop();
            if (PathGuard.IsReparsePoint(current))
            {
                block ??= "Contains a junction or symbolic link, so it is kept.";
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(current);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                status = ScanStatus.Partial;
                block ??= "Part of this folder could not be read, so it is kept.";
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var itemName = Path.GetFileName(entry);
                if (PathGuard.IsReparsePoint(entry))
                {
                    block ??= "Contains a junction or symbolic link, so it is kept.";
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    if (string.Equals(itemName, ".git", StringComparison.OrdinalIgnoreCase))
                    {
                        block ??= "Contains a nested source repository, so it is kept.";
                        continue;
                    }

                    try
                    {
                        var modified = Directory.GetLastWriteTimeUtc(entry);
                        if (latest is null || modified > latest)
                        {
                            latest = modified;
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        status = ScanStatus.Partial;
                        block ??= "Activity could not be verified, so it is kept.";
                    }

                    pending.Push(entry);
                    continue;
                }

                if (string.Equals(itemName, ".git", StringComparison.OrdinalIgnoreCase) && File.Exists(entry))
                {
                    block ??= "Contains a nested source repository, so it is kept.";
                    continue;
                }

                try
                {
                    var info = new FileInfo(entry);
                    size += info.Length;
                    files++;
                    if (latest is null || info.LastWriteTimeUtc > latest)
                    {
                        latest = info.LastWriteTimeUtc;
                    }

                    if (IsSensitiveFile(itemName))
                    {
                        block ??= "Contains a credentials or private-key file, so it is kept.";
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    status = ScanStatus.Partial;
                    block ??= "A file could not be measured, so this folder is kept.";
                }
            }
        }

        timer.Stop();

        // For git-backed projects, unknown Git state is a hold, not permission to delete.
        if (block is null && IsGitRepository(projectRoot))
        {
            var tracking = HasTrackedFiles(projectRoot, path);
            if (tracking is null)
            {
                block = "Git could not verify whether this folder contains tracked files, so it is kept.";
            }
            else if (tracking.Value)
            {
                block = "Contains Git-tracked files, so it is kept.";
            }
        }

        var oldEnough = latest is not null && DateTime.UtcNow - latest.Value >= _minimumAge;
        if (block is null && !oldEnough)
        {
            block = latest is null
                ? "Activity could not be verified, so it is kept."
                : $"Changed within the last {_minimumAge.TotalDays:0} days, so it is kept.";
        }

        if (!PathGuard.Evaluate(path).Allowed)
        {
            block ??= "This path is protected by the system safety rules.";
        }

        return new ProjectArtifact
        {
            ProjectName = Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar)),
            ProjectPath = projectRoot,
            Name = name,
            Path = path,
            Description = description,
            SizeBytes = size,
            SizeStatus = status,
            LastActivityUtc = latest,
            BlockedReason = block,
            IsSelected = status == ScanStatus.Complete && block is null && files > 0
        };
    }

    private static bool HasProjectMarker(string directory, IReadOnlyList<string> entries)
    {
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (ProjectMarkers.Contains(name))
            {
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase) && PathGuard.IsReparsePoint(entry))
                {
                    continue;
                }

                return true;
            }

            if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsArtifactForProject(string projectRoot, string artifactName)
    {
        if (!artifactName.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
            !artifactName.Equals("obj", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // `bin` is a source-code folder in SDKs such as Flutter and in many scripts.
        // Only treat it as build output when the project itself declares .NET output.
        try
        {
            return Directory.EnumerateFiles(projectRoot, "*.csproj", SearchOption.TopDirectoryOnly).Any() ||
                   Directory.EnumerateFiles(projectRoot, "*.fsproj", SearchOption.TopDirectoryOnly).Any() ||
                   Directory.EnumerateFiles(projectRoot, "*.vbproj", SearchOption.TopDirectoryOnly).Any() ||
                   Directory.EnumerateFiles(projectRoot, "*.sln", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static ProjectArtifact CopyWithBlock(ProjectArtifact artifact, string reason) => new()
    {
        ProjectName = artifact.ProjectName,
        ProjectPath = artifact.ProjectPath,
        Name = artifact.Name,
        Path = artifact.Path,
        Description = artifact.Description,
        SizeBytes = artifact.SizeBytes,
        SizeStatus = artifact.SizeStatus,
        LastActivityUtc = artifact.LastActivityUtc,
        BlockedReason = reason,
        IsSelected = false
    };

    private static bool IsSensitiveFile(string name)
    {
        if (SensitiveNames.Contains(name))
        {
            return true;
        }

        var extension = Path.GetExtension(name);
        return extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".p12", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGitRepository(string projectRoot) =>
        Directory.Exists(Path.Combine(projectRoot, ".git")) || File.Exists(Path.Combine(projectRoot, ".git"));

    /// <returns>True if tracked files were found; false if none; null if Git could not verify.</returns>
    private static bool? HasTrackedFiles(string projectRoot, string artifactPath)
    {
        var git = Environment.GetEnvironmentVariable("ProgramFiles") is { Length: > 0 } programFiles
            ? Path.Combine(programFiles, "Git", "cmd", "git.exe")
            : "git.exe";

        if (!File.Exists(git) && git != "git.exe")
        {
            git = "git.exe";
        }

        try
        {
            var relative = Path.GetRelativePath(projectRoot, artifactPath);
            var start = new ProcessStartInfo
            {
                FileName = git,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(projectRoot);
            start.ArgumentList.Add("ls-files");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(relative);

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            // Drain redirected pipes while the process runs. Waiting first can deadlock
            // when a generated folder has many tracked entries and fills the pipe buffer.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(1000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }

            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? !string.IsNullOrWhiteSpace(output) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
