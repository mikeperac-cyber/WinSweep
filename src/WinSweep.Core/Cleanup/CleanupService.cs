using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;
using WinSweep.Core.Services;
using WinSweep.Core.Settings;

namespace WinSweep.Core.Cleanup;

/// <summary>The outcome of one cleanup run.</summary>
public sealed class CleanupResult
{
    public int RemovedCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
    public long BytesReclaimed { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Removes the reviewed cleanup targets. Every removal passes the safety guard again
/// here, so a change between the review and the run cannot slip past.
/// </summary>
public sealed class CleanupService
{
    private readonly OperationLog _log;
    private readonly AppSettings _settings;
    private readonly Func<string, long, RemovalResult>? _remove;
    private readonly OperationAction _removedAction;

    /// <param name="remove">Replaces the Recycle Bin / permanent delete, for example the quarantine vault.</param>
    /// <param name="removedAction">What a successful <paramref name="remove"/> is logged as.</param>
    public CleanupService(
        OperationLog log,
        AppSettings settings,
        Func<string, long, RemovalResult>? remove = null,
        OperationAction removedAction = OperationAction.Removed)
    {
        _log = log;
        _settings = settings;
        _remove = remove;
        _removedAction = removedAction;
    }

    public CleanupResult Execute(
        IReadOnlyList<CleanupCandidate> candidates,
        string runId,
        CancellationToken cancellationToken = default)
    {
        int removed = 0;
        int failed = 0;
        int skipped = 0;
        long reclaimed = 0;
        var failures = new List<string>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!candidate.IsSelected)
            {
                skipped++;
                _log.Append(new OperationRecord
                {
                    Command = "clean",
                    RunId = runId,
                    Action = OperationAction.Skipped,
                    Path = candidate.Path,
                    Detail = candidate.BlockedReason ?? "Not selected for removal"
                });
                continue;
            }

            var verdict = PathGuard.Evaluate(candidate.Path);
            if (!verdict.Allowed)
            {
                skipped++;
                _log.Append(new OperationRecord
                {
                    Command = "clean",
                    RunId = runId,
                    Action = OperationAction.Skipped,
                    Path = candidate.Path,
                    Detail = verdict.Reason
                });
                continue;
            }

            if (_settings.IsProtected(candidate.Path))
            {
                skipped++;
                _log.Append(new OperationRecord
                {
                    Command = "clean",
                    RunId = runId,
                    Action = OperationAction.Skipped,
                    Path = candidate.Path,
                    Detail = "Protected by your settings"
                });
                continue;
            }

            var result = _remove is not null
                ? _remove(candidate.Path, candidate.SizeBytes)
                : _settings.UseRecycleBin
                    ? RecycleBin.Send(candidate.Path, candidate.SizeBytes)
                    : RecycleBin.DeletePermanently(candidate.Path, candidate.SizeBytes);

            if (result.Succeeded)
            {
                removed++;
                reclaimed += result.BytesReclaimed;
                _log.Append(new OperationRecord
                {
                    Command = "clean",
                    RunId = runId,
                    Action = _remove is not null
                        ? _removedAction
                        : _settings.UseRecycleBin ? OperationAction.Recycled : OperationAction.Removed,
                    Path = candidate.Path,
                    Detail = candidate.Name,
                    SizeBytes = result.BytesReclaimed
                });
            }
            else
            {
                failed++;
                failures.Add($"{candidate.Name}: {result.FailureReason}");
                _log.Append(new OperationRecord
                {
                    Command = "clean",
                    RunId = runId,
                    Action = OperationAction.Failed,
                    Path = candidate.Path,
                    Detail = result.FailureReason ?? "Unknown failure"
                });
            }
        }

        return new CleanupResult
        {
            RemovedCount = removed,
            FailedCount = failed,
            SkippedCount = skipped,
            BytesReclaimed = reclaimed,
            Failures = failures
        };
    }
}
