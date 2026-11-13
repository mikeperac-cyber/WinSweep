using System.Diagnostics;
using WinSweep.Core.Safety;

namespace WinSweep.Core.Scanning;

/// <summary>Estimates when a project was last worked on, from Git history and source file times.</summary>
public static class ProjectActivity
{
    private const int MaxFiles = 20000;
    private static readonly TimeSpan WalkBudget = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> SkipDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea", "vendor", "packages", "Pods"
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".rs", ".cs", ".fs", ".vb", ".go", ".java",
        ".kt", ".c", ".cc", ".cpp", ".h", ".hpp", ".swift", ".rb", ".php", ".html", ".css", ".scss",
        ".vue", ".svelte", ".xaml", ".sql", ".sh", ".ps1", ".json", ".toml", ".yaml", ".yml",
        ".csproj", ".fsproj", ".sln"
    };

    /// <summary>
    /// The newest of: the last Git commit, and the newest last-write time of a source file outside
    /// build output. Null when neither could be established (or the source walk ran out of budget),
    /// which callers must treat as "active".
    /// </summary>
    public static DateTime? LastActivityUtc(string projectRoot, CancellationToken cancellationToken = default)
    {
        var (source, complete) = WalkSources(projectRoot, cancellationToken);
        if (!complete)
        {
            return null; // An unfinished walk may have missed a newer file, so an old commit proves nothing.
        }

        var commit = LastCommitUtc(projectRoot);
        return commit is null ? source : source is null || commit > source ? commit : source;
    }

    /// <summary>The last Git commit time, or null: not a repo, no commits, git missing, or slower than 1 s.</summary>
    public static DateTime? LastCommitUtc(string projectRoot)
    {
        var marker = Path.Combine(projectRoot, ".git");
        if (!Directory.Exists(marker) && !File.Exists(marker))
        {
            return null;
        }

        var output = RunGit(projectRoot, "log", "-1", "--format=%ct");
        if (!long.TryParse(output?.Trim(), out var seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The newest source file write time outside build output, or null when none or over budget.</summary>
    public static DateTime? LatestSourceWriteUtc(string projectRoot, CancellationToken cancellationToken = default) =>
        WalkSources(projectRoot, cancellationToken) is { Complete: true } walk ? walk.Latest : null;

    private static (DateTime? Latest, bool Complete) WalkSources(string projectRoot, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + WalkBudget;
        var pending = new Stack<string>();
        var latest = (DateTime?)null;
        var files = 0;
        pending.Push(projectRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = pending.Pop();
            if (PathGuard.IsReparsePoint(directory))
            {
                continue;
            }

            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        return (null, false);
                    }

                    if (entry is DirectoryInfo)
                    {
                        if (!SkipDirectoryNames.Contains(entry.Name) && !ProjectArtifactScanner.IsRecognizedArtifact(entry.Name))
                        {
                            pending.Push(entry.FullName);
                        }
                    }
                    else if (++files > MaxFiles)
                    {
                        return (null, false);
                    }
                    else if (SourceExtensions.Contains(entry.Extension) || ProjectArtifactScanner.ProjectMarkers.Contains(entry.Name))
                    {
                        var written = entry.LastWriteTimeUtc;
                        if (latest is null || written > latest)
                        {
                            latest = written;
                        }
                    }
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Unreadable directory: skipped.
            }
        }

        return (latest, true);
    }

    /// <summary>Runs git in <paramref name="projectRoot"/>; returns stdout, or null on any failure, non-zero exit or after 1 s.</summary>
    internal static string? RunGit(string projectRoot, params string[] args)
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
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            // Drain redirected pipes while the process runs. Waiting first can deadlock
            // when the output fills the pipe buffer.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(1000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }

            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
