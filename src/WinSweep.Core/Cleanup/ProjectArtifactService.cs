using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;
using WinSweep.Core.Scanning;
using WinSweep.Core.Services;
using WinSweep.Core.Settings;

namespace WinSweep.Core.Cleanup;

public sealed class ProjectArtifactCleanupResult
{
    public int RecycledCount { get; init; }
    public int SkippedCount { get; init; }
    public int FailedCount { get; init; }
    public long BytesReclaimed { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
}

/// <summary>Recycles only selected, re-verified project artifacts.</summary>
public sealed class ProjectArtifactService
{
    private readonly OperationLog _log;
    private readonly AppSettings _settings;
    private readonly Func<string, long, RemovalResult> _recycle;
    private readonly OperationAction _recycledAction;
    private readonly ProjectArtifactScanner _scanner;

    public ProjectArtifactService(
        OperationLog log,
        AppSettings settings,
        Func<string, long, RemovalResult>? recycle = null,
        OperationAction recycledAction = OperationAction.Recycled)
    {
        _log = log;
        _settings = settings;
        _recycle = recycle ?? RecycleBin.Send;
        _recycledAction = recycledAction;
        _scanner = new ProjectArtifactScanner(
            scanBudget: TimeSpan.FromSeconds(5),
            artifactBudget: TimeSpan.FromSeconds(2),
            projectInactivity: TimeSpan.FromDays(Math.Max(1, settings.ProjectInactivityDays)));
    }

    public ProjectArtifactCleanupResult Execute(
        IReadOnlyList<ProjectArtifact> artifacts,
        string runId,
        CancellationToken cancellationToken = default)
    {
        var recycled = 0;
        var skipped = 0;
        var failed = 0;
        long bytes = 0;
        var failures = new List<string>();

        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!artifact.IsSelected)
            {
                skipped++;
                Log(artifact, runId, OperationAction.Skipped, artifact.BlockedReason ?? "Not selected for cleanup", 0);
                continue;
            }

            var verdict = PathGuard.Evaluate(artifact.Path);
            if (!verdict.Allowed || !IsExpectedArtifactLocation(artifact))
            {
                skipped++;
                var reason = !verdict.Allowed ? verdict.Reason : "The path is no longer a recognized project artifact.";
                Log(artifact, runId, OperationAction.Skipped, reason, 0);
                continue;
            }

            if (_settings.IsProtected(artifact.Path))
            {
                skipped++;
                Log(artifact, runId, OperationAction.Skipped, "Protected by your settings", 0);
                continue;
            }

            if (!Directory.Exists(artifact.Path) || PathGuard.HasReparsePointInPath(artifact.Path))
            {
                skipped++;
                Log(artifact, runId, OperationAction.Skipped, "The folder disappeared or now crosses a junction", 0);
                continue;
            }

            var verified = _scanner.Revalidate(artifact, cancellationToken);
            if (verified.BlockedReason is not null ||
                verified.SizeStatus != ScanStatus.Complete ||
                !verified.IsSelected)
            {
                skipped++;
                Log(artifact, runId, OperationAction.Skipped,
                    verified.BlockedReason ?? "The folder could not be fully rechecked, so it is kept", 0);
                continue;
            }

            var result = _recycle(artifact.Path, verified.SizeBytes);
            if (result.Succeeded)
            {
                recycled++;
                bytes += result.BytesReclaimed;
                Log(artifact, runId, _recycledAction,
                    _recycledAction == OperationAction.Quarantined ? "Moved to the quarantine vault" : "Moved to the Recycle Bin",
                    result.BytesReclaimed);
            }
            else
            {
                failed++;
                var detail = result.FailureReason ?? (_recycledAction == OperationAction.Quarantined
                    ? "The folder could not be moved to the quarantine vault"
                    : "The folder could not be moved to the Recycle Bin");
                failures.Add($"{artifact.Path}: {detail}");
                Log(artifact, runId, OperationAction.Failed, detail, 0);
            }
        }

        return new ProjectArtifactCleanupResult
        {
            RecycledCount = recycled,
            SkippedCount = skipped,
            FailedCount = failed,
            BytesReclaimed = bytes,
            Failures = failures
        };
    }

    private static bool IsExpectedArtifactLocation(ProjectArtifact artifact)
    {
        try
        {
            var root = PathGuard.Normalize(artifact.ProjectPath);
            var target = PathGuard.Normalize(artifact.Path);
            return string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetFileName(target), artifact.Name, StringComparison.OrdinalIgnoreCase) &&
                   ProjectArtifactScanner.IsRecognizedArtifact(artifact.Name) &&
                   ProjectArtifactScanner.HasProjectMarker(root);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Log(ProjectArtifact artifact, string runId, OperationAction action, string detail, long size)
    {
        _log.Append(new OperationRecord
        {
            Command = "purge",
            RunId = runId,
            Action = action,
            Path = artifact.Path,
            Detail = detail,
            SizeBytes = size
        });
    }
}
