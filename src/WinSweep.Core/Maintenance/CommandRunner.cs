using System.Diagnostics;
using System.Text;

namespace WinSweep.Core.Maintenance;

/// <summary>
/// Runs one external command with a hard time limit. The command is always given as a
/// program and its arguments separately, never as a shell string, so nothing can be
/// injected into a shell by accident.
/// </summary>
public static class CommandRunner
{
    public static async Task<MaintenanceResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Tasks that need an administrator account only run once this process is already
        // elevated (see MaintenanceService), so the child inherits that token and its output
        // can still be captured.
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return MaintenanceResult.Failed("Windows could not start the task.");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                cancellationToken.ThrowIfCancellationRequested(); // the user cancelled, not a timeout
                return MaintenanceResult.Failed(
                    $"The task did not finish within {timeout.TotalMinutes:0.#} minutes and was stopped.");
            }

            var output = await stdout + await stderr;
            return process.ExitCode == 0
                ? MaintenanceResult.Applied("Done.", output.Trim())
                : MaintenanceResult.Failed($"The task failed with exit code {process.ExitCode}.", output.Trim(), process.ExitCode);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return MaintenanceResult.Failed(e.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process already ended.
        }
    }

    /// <summary>Reads a value the way Windows command-line tools print it.</summary>
    public static string CleanOutput(string output)
    {
        var lines = output
            .Split('\n')
            .Select(l => l.Trim('\r', ' ', '\t'))
            .Where(l => l.Length > 0)
            .ToList();

        return string.Join(Environment.NewLine, lines);
    }
}
