using System.Text.RegularExpressions;
using WinSweep.Core.Maintenance;

namespace WinSweep.Core.Sweep;

/// <summary>Takes an optional Windows shadow copy of the system drive before a sweep.</summary>
public static class VolumeSnapshot
{
    public const string NotElevatedMessage =
        "No Windows snapshot was taken because WinSweep is not running as administrator. The quarantine vault can still undo this sweep.";

    private static readonly Regex DrivePattern = new(@"^[A-Za-z]:\z", RegexOptions.Compiled);

    /// <summary>
    /// Creates a shadow copy when <paramref name="isElevated"/>; otherwise starts nothing. A failure is
    /// reported in the result and never thrown, so a sweep is not blocked by it.
    /// </summary>
    public static async Task<SnapshotInfo> CreateAsync(bool isElevated, CancellationToken cancellationToken = default)
    {
        if (!isElevated)
        {
            return new SnapshotInfo(false, false, NotElevatedMessage);
        }

        var drive = Environment.GetEnvironmentVariable("SystemDrive");
        if (drive is null || !DrivePattern.IsMatch(drive))
        {
            drive = "C:";
        }

        var result = await CommandRunner.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"(Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create " +
            $"-Arguments @{{Volume='{drive}\\'; Context='ClientAccessible'}}).ReturnValue\"",
            TimeSpan.FromMinutes(3),
            cancellationToken);

        if (result.Outcome == MaintenanceOutcome.Applied && result.Output.Trim() == "0")
        {
            return new SnapshotInfo(true, true, $"A Windows shadow copy of {drive} was created.");
        }

        var why = result.Outcome == MaintenanceOutcome.Applied
            ? (result.Output.Trim().Length == 0 ? "it returned no result" : $"it returned code {result.Output.Trim()}")
            : result.Message;
        return new SnapshotInfo(true, false,
            $"Windows could not create a snapshot: {why}. The quarantine vault can still undo this sweep.");
    }
}
