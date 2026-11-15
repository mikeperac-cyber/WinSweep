using System.Text.RegularExpressions;
using WinSweep.Core.Logging;
using WinSweep.Core.Maintenance;
using WinSweep.Core.Models;

namespace WinSweep.Core.Apps;

/// <summary>An installed app that winget can update.</summary>
public sealed record AppUpdate(string Name, string Id, string Version, string Available);

/// <summary>Lists and installs app updates through the Windows Package Manager (winget).</summary>
public sealed partial class AppUpdateService
{
    private const string CommonArguments = "--accept-source-agreements --disable-interactivity";

    private readonly OperationLog _log;

    public AppUpdateService(OperationLog log)
    {
        _log = log;
    }

    /// <summary>Asks winget which installed apps have a newer version.</summary>
    public async Task<(IReadOnlyList<AppUpdate> Updates, string? Error)> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await CommandRunner.RunAsync(
            "winget.exe",
            $"upgrade --include-unknown {CommonArguments}",
            TimeSpan.FromMinutes(2),
            cancellationToken);

        var updates = ParseUpgradeTable(result.Output);
        if (updates.Count > 0)
        {
            return (updates, null);
        }

        return (updates, InterpretEmptyResult(result));
    }

    /// <summary>
    /// Explains what an empty upgrade table means, or null when the machine really is up
    /// to date. winget exits non-zero when there is nothing to update, so the exit code
    /// alone cannot tell "all current" from "the check failed". Reporting an unreadable
    /// result as success would tell the user they are fully patched when the check never
    /// worked, so anything this cannot classify is surfaced as an error.
    /// </summary>
    public static string? InterpretEmptyResult(MaintenanceResult result)
    {
        // Exit code 0 with no rows: everything winget knows about is current.
        if (result.Outcome == MaintenanceOutcome.Applied)
        {
            return null;
        }

        // The process ran but printed no table. winget signals "nothing to update" with a
        // non-zero exit and no output, so only text here means the check itself failed.
        if (result.ExitCode is not null)
        {
            var detail = FirstMeaningfulLine(result.Output);
            return detail.Length == 0
                ? null
                : $"winget could not list app updates: {detail}";
        }

        // No exit code means the process never started, so name the real problem.
        var reason = FirstMeaningfulLine(result.Message);
        return reason.Length == 0
            ? "winget (App Installer) is not available on this PC."
            : $"winget could not be run: {reason}";
    }

    /// <summary>The first non-empty line of a tool's own output, trimmed for display.</summary>
    private static string FirstMeaningfulLine(string text) =>
        text.Split('\n')
            .Select(l => l.Trim('\r', ' ', '\t'))
            .FirstOrDefault(l => l.Length > 0) ?? string.Empty;

    /// <summary>Installs the newer version of one app and records it in the history.</summary>
    public async Task<MaintenanceResult> UpdateAsync(AppUpdate update, string runId, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(update.Id))
        {
            return MaintenanceResult.Unavailable("This app cannot be updated from here.");
        }

        var result = await CommandRunner.RunAsync(
            "winget.exe",
            $"upgrade --id {update.Id} --exact --silent --accept-package-agreements {CommonArguments}",
            TimeSpan.FromMinutes(20),
            cancellationToken);

        _log.Append(new OperationRecord
        {
            Command = "update",
            RunId = runId,
            Action = result.Outcome == MaintenanceOutcome.Applied ? OperationAction.Completed : OperationAction.Failed,
            Path = update.Id,
            Detail = $"{update.Name}: {update.Version} → {update.Available}" +
                     (result.Outcome == MaintenanceOutcome.Applied ? string.Empty : $" ({result.Message})")
        });

        return result;
    }

    /// <summary>
    /// Reads the fixed-width table that <c>winget upgrade</c> prints. Columns are located by
    /// the header words above the dashed rule, so localized headers work too.
    /// shortcut: assumes one character per column cell; names with wide (CJK) characters can
    /// misalign. Switch to the winget COM API if that shows up in practice.
    /// </summary>
    public static IReadOnlyList<AppUpdate> ParseUpgradeTable(string output)
    {
        // winget redraws a progress spinner with carriage returns; keep what was drawn last.
        var lines = output
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Select(l => l[(l.LastIndexOf('\r') + 1)..])
            .ToList();

        var rule = lines.FindIndex(l => l.TrimStart().StartsWith("---", StringComparison.Ordinal));
        if (rule < 1)
        {
            return Array.Empty<AppUpdate>();
        }

        var header = lines[rule - 1];
        var starts = ColumnStart().Matches(header).Select(m => m.Index).ToList();
        if (starts.Count < 4)
        {
            return Array.Empty<AppUpdate>();
        }

        var updates = new List<AppUpdate>();
        foreach (var line in lines.Skip(rule + 1))
        {
            // The table ends at the first blank line; anything after it (the summary or the
            // "require explicit targeting" table) is not updatable with a plain upgrade.
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            var update = new AppUpdate(
                Cell(line, starts, 0),
                Cell(line, starts, 1),
                Cell(line, starts, 2),
                Cell(line, starts, 3));

            if (IsValidId(update.Id) && update.Available.Length > 0)
            {
                updates.Add(update);
            }
        }

        return updates;
    }

    private static string Cell(string line, List<int> starts, int column)
    {
        var start = starts[column];
        if (start >= line.Length)
        {
            return string.Empty;
        }

        var end = column + 1 < starts.Count ? Math.Min(starts[column + 1], line.Length) : line.Length;
        return line[start..end].Trim();
    }

    // The id is passed on the command line, so only plain package ids get through.
    private static bool IsValidId(string id) => ValidId().IsMatch(id);

    [GeneratedRegex(@"^[\w.\-+]+$")]
    private static partial Regex ValidId();

    [GeneratedRegex(@"(?<=^|\s)\S")]
    private static partial Regex ColumnStart();
}
