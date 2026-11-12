using System.Diagnostics;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;

namespace WinSweep.Core.Scanning;

/// <summary>A file that crossed the large-file threshold.</summary>
public readonly record struct LargeFile(string Path, long Length, DateTime? LastWriteTimeUtc);

/// <summary>The outcome of measuring a folder.</summary>
public sealed record SizeMeasurement(
    long Bytes,
    long FileCount,
    ScanStatus Status,
    IReadOnlyList<string> UnreadablePaths)
{
    public static SizeMeasurement Unavailable(string path) => new(0, 0, ScanStatus.Unavailable, new[] { path });
}

/// <summary>
/// Measures folders without lying about the result. A subtree that cannot be read
/// produces a lower bound and a partial status, never a silent zero.
/// </summary>
public static class DirectorySizer
{
    public static SizeMeasurement Measure(
        string path,
        CancellationToken cancellationToken = default,
        TimeSpan? budget = null,
        int maxDepth = 64,
        List<LargeFile>? largeFiles = null,
        long largeFileThresholdBytes = 0)
    {
        if (!Directory.Exists(path))
        {
            return SizeMeasurement.Unavailable(path);
        }

        var deadline = budget is null ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + budget.Value;
        var unreadable = new List<string>();
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((path, 0));

        long bytes = 0;
        long files = 0;
        var partial = false;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow > deadline)
            {
                partial = true;
                break;
            }

            var (current, depth) = stack.Pop();
            if (depth > maxDepth)
            {
                partial = true;
                continue;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(current);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                unreadable.Add(current);
                partial = true;
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var attributes = File.GetAttributes(child);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // A link can point anywhere; it is never followed and never counted.
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        stack.Push((child, depth + 1));
                    }
                    else
                    {
                        files++;
                        var length = new FileInfo(child).Length;
                        bytes += length;

                        if (largeFiles is not null && length >= largeFileThresholdBytes)
                        {
                            largeFiles.Add(new LargeFile(child, length, SafeLastWrite(child)));
                        }
                    }
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                {
                    unreadable.Add(child);
                    partial = true;
                }
            }
        }

        var status = partial ? ScanStatus.Partial : ScanStatus.Complete;
        return new SizeMeasurement(bytes, files, status, unreadable);
    }

    /// <summary>Measures the size of a single file without failing when it is locked.</summary>
    public static SizeMeasurement MeasureFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return SizeMeasurement.Unavailable(path);
            }

            return new SizeMeasurement(info.Length, 1, ScanStatus.Complete, Array.Empty<string>());
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return SizeMeasurement.Unavailable(path);
        }
    }

    private static DateTime? SafeLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
