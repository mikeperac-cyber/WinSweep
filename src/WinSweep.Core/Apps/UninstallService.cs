using System.Diagnostics;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;
using WinSweep.Core.Services;

namespace WinSweep.Core.Apps;

/// <summary>The outcome of running an app's own uninstaller.</summary>
public sealed record UninstallOutcome(bool Succeeded, int? ExitCode, string Message);

/// <summary>
/// Runs the uninstaller the app vendor registered, then offers the verified leftover
/// install folder for removal. The app's own uninstaller decides what it removes.
/// </summary>
public sealed class UninstallService
{
    private readonly OperationLog _log;

    public UninstallService(OperationLog log)
    {
        _log = log;
    }

    /// <summary>Starts the registered uninstaller and waits for it to finish.</summary>
    public UninstallOutcome RunUninstaller(InstalledApp app, bool preferSilent, string runId, CancellationToken cancellationToken = default)
    {
        var command = preferSilent && !string.IsNullOrWhiteSpace(app.QuietUninstallString)
            ? app.QuietUninstallString
            : app.UninstallString;

        if (string.IsNullOrWhiteSpace(command))
        {
            return new UninstallOutcome(false, null, "This app has no registered uninstaller.");
        }

        var (executable, arguments) = SplitCommand(command);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new UninstallOutcome(false, null, "The registered uninstall command could not be read.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new UninstallOutcome(false, null, "Windows could not start the uninstaller.");
            }

            process.WaitForExit();

            _log.Append(new OperationRecord
            {
                Command = "uninstall",
                RunId = runId,
                Action = process.ExitCode == 0 ? OperationAction.Uninstalled : OperationAction.Failed,
                Path = app.InstallLocation,
                Detail = $"{app.Name} exited with code {process.ExitCode}",
                SizeBytes = app.SizeBytes ?? 0
            });

            return process.ExitCode == 0
                ? new UninstallOutcome(true, process.ExitCode, $"{app.Name} was uninstalled.")
                : new UninstallOutcome(false, process.ExitCode,
                    $"{app.Name} exited with code {process.ExitCode}. Its files may still be present.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Append(new OperationRecord
            {
                Command = "uninstall",
                RunId = runId,
                Action = OperationAction.Failed,
                Path = app.InstallLocation,
                Detail = e.Message
            });

            return new UninstallOutcome(false, null, e.Message);
        }
    }

    /// <summary>
    /// Moves the install folder to the Recycle Bin after the uninstaller ran. The folder
    /// must still exist and must not be a protected location.
    /// </summary>
    public RemovalResult RemoveInstallFolder(InstalledApp app, long sizeBytes, string runId)
    {
        var path = app.InstallLocation;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return RemovalResult.Failed("The install folder is already gone");
        }

        var verdict = PathGuard.Evaluate(path);
        if (!verdict.Allowed)
        {
            _log.Append(new OperationRecord
            {
                Command = "uninstall",
                RunId = runId,
                Action = OperationAction.Skipped,
                Path = path,
                Detail = verdict.Reason
            });

            return RemovalResult.Failed(verdict.Reason);
        }

        var result = RecycleBin.Send(path, sizeBytes);

        _log.Append(new OperationRecord
        {
            Command = "uninstall",
            RunId = runId,
            Action = result.Succeeded ? OperationAction.Recycled : OperationAction.Failed,
            Path = path,
            Detail = result.Succeeded ? "Install folder moved to the Recycle Bin" : result.FailureReason ?? "Unknown failure",
            SizeBytes = result.Succeeded ? sizeBytes : 0
        });

        return result;
    }

    /// <summary>Splits a command string into the executable and its arguments.</summary>
    public static (string Executable, string Arguments) SplitCommand(string command)
    {
        var trimmed = command.Trim();

        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            if (closing > 0)
            {
                var executable = trimmed[1..closing];
                var arguments = trimmed[(closing + 1)..].Trim();
                return (executable, arguments);
            }
        }

        // An unquoted command: the first token is the executable.
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0)
        {
            return (trimmed[..space], trimmed[(space + 1)..].Trim());
        }

        return (trimmed, string.Empty);
    }
}
