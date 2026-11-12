using WinSweep.Core.Safety;

namespace WinSweep.Core.Services;

/// <summary>The result of one removal attempt.</summary>
public readonly record struct RemovalResult(bool Succeeded, long BytesReclaimed, string? FailureReason)
{
    public static RemovalResult Ok(long bytes) => new(true, bytes, null);
    public static RemovalResult Failed(string reason) => new(false, 0, reason);
}

/// <summary>Moves items to the Recycle Bin so the user can recover them.</summary>
public static class RecycleBin
{
    /// <summary>
    /// Sends a file or folder to the Recycle Bin. Falls back to a permanent delete only
    /// when the shell API is unavailable, which is reported to the caller.
    /// </summary>
    public static RemovalResult Send(string path, long sizeBytes)
    {
        if (!PathGuard.Evaluate(path).Allowed)
        {
            return RemovalResult.Failed("Refused by the safety guard");
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return RemovalResult.Failed("The item no longer exists");
        }

        try
        {
            var didRecycle = TrySendToRecycleBin(path);
            if (!didRecycle)
            {
                return RemovalResult.Failed("Windows could not move the item to the Recycle Bin");
            }

            return RemovalResult.Ok(sizeBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return RemovalResult.Failed(e.Message);
        }
    }

    private static bool TrySendToRecycleBin(string path)
    {
        // Microsoft.VisualBasic.FileIO routes to the shell's Recycle Bin implementation,
        // which keeps the recovery metadata a user expects.
        var isDirectory = Directory.Exists(path);
        var option = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
        var recycle = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
        var conflict = Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException;

        if (isDirectory)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path, option, recycle, conflict);
        }
        else
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path, option, recycle, conflict);
        }

        return true;
    }

    /// <summary>Deletes permanently. Only used for caches that the user explicitly opted out of the Recycle Bin.</summary>
    public static RemovalResult DeletePermanently(string path, long sizeBytes)
    {
        if (!PathGuard.Evaluate(path).Allowed)
        {
            return RemovalResult.Failed("Refused by the safety guard");
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
            else
            {
                return RemovalResult.Failed("The item no longer exists");
            }

            return RemovalResult.Ok(sizeBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return RemovalResult.Failed(e.Message);
        }
    }
}
