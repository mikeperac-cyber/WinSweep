using System.Diagnostics;
using WinSweep.Core.Models;
using WinSweep.Core.Safety;

namespace WinSweep.Core.Scanning;

/// <summary>Options for one disk scan.</summary>
public sealed class DiskScanOptions
{
    /// <summary>Wall-clock budget for the whole scan.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How many of the largest files to report.</summary>
    public int LargeFileLimit { get; init; } = 50;

    /// <summary>A file must be at least this large to count as a large file.</summary>
    public long LargeFileThresholdBytes { get; init; } = 100L * 1024 * 1024;

    public int MaxDepth { get; init; } = 64;
}

/// <summary>Walks a folder tree and reports sizes, large files and what could not be read.</summary>
public sealed class DiskScanner
{
    private static readonly string[] InsightNames =
    {
        "Downloads", "Temp", "tmp", "Cache", "Caches", "node_modules", "AppData",
        "Local", "Roaming", "Package Cache", "Installer", "packages", "target", "build",
        "dist", ".nuget", ".gradle", ".npm", ".cargo", "Library", "obj", "bin"
    };

    public DiskScanResult Scan(string path, DiskScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DiskScanOptions();
        var stopwatch = Stopwatch.StartNew();
        var root = PathGuard.Normalize(path);

        if (!Directory.Exists(root))
        {
            return new DiskScanResult
            {
                Path = root,
                Status = ScanStatus.Unavailable,
                UnreadablePaths = new[] { root }
            };
        }

        var deadline = DateTimeOffset.UtcNow + options.Budget;
        var unreadable = new List<string>();
        var entries = new List<DiskEntry>();
        var largeFiles = new List<DiskEntry>();
        var nestedLargeFiles = new List<LargeFile>();
        long totalBytes = 0;
        long totalFiles = 0;
        var partial = false;
        var timedOut = false;

        // Measure the children of the requested folder; the folder itself is the container.
        var childDirectories = new List<string>();
        var childFiles = new List<(string Path, long Length, DateTime? LastWrite)>();

        try
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(root))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (DateTimeOffset.UtcNow > deadline)
                {
                    partial = true;
                    break;
                }

                try
                {
                    var attributes = File.GetAttributes(child);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        childDirectories.Add(child);
                    }
                    else
                    {
                        var info = new FileInfo(child);
                        childFiles.Add((child, info.Length, info.LastWriteTimeUtc));
                    }
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                {
                    unreadable.Add(child);
                    partial = true;
                }
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            unreadable.Add(root);
            partial = true;
        }

        foreach (var (filePath, length, lastWrite) in childFiles)
        {
            totalBytes += length;
            totalFiles++;

            var entry = new DiskEntry
            {
                Name = Path.GetFileName(filePath),
                Path = filePath,
                SizeBytes = length,
                IsDirectory = false,
                SizeStatus = ScanStatus.Complete,
                LastWriteTimeUtc = lastWrite
            };

            entries.Add(entry);
            if (length >= options.LargeFileThresholdBytes)
            {
                largeFiles.Add(entry);
            }
        }

        var pending = new List<string>(childDirectories);
        var index = 0;

        while (index < pending.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                partial = true;
                timedOut = true;
                break;
            }

            var directory = pending[index];
            index++;

            // Every folder gets a share of what is left, so one huge folder cannot
            // starve the rest of the listing.
            var share = remaining / Math.Max(1, pending.Count - index + 1);
            if (share < TimeSpan.FromMilliseconds(250))
            {
                share = TimeSpan.FromMilliseconds(250);
            }

            var measurement = DirectorySizer.Measure(
                directory,
                cancellationToken,
                share,
                options.MaxDepth,
                nestedLargeFiles,
                options.LargeFileThresholdBytes);

            totalBytes += measurement.Bytes;
            totalFiles += measurement.FileCount;

            if (measurement.Status == ScanStatus.Partial)
            {
                partial = true;
            }

            unreadable.AddRange(measurement.UnreadablePaths);

            DateTime? lastWrite = null;
            try
            {
                lastWrite = Directory.GetLastWriteTimeUtc(directory);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // A timestamp is a nicety; its absence does not change the size result.
            }

            entries.Add(new DiskEntry
            {
                Name = Path.GetFileName(directory),
                Path = directory,
                SizeBytes = measurement.Bytes,
                IsDirectory = true,
                SizeStatus = measurement.Status,
                LastWriteTimeUtc = lastWrite,
                IsInsight = IsInsightFolder(directory)
            });
        }

        // Folders that never got a share are listed with an unknown size, never zero.
        for (; index < pending.Count; index++)
        {
            partial = true;
            timedOut = true;
            entries.Add(new DiskEntry
            {
                Name = Path.GetFileName(pending[index]),
                Path = pending[index],
                SizeBytes = 0,
                IsDirectory = true,
                SizeStatus = ScanStatus.Unavailable,
                IsInsight = IsInsightFolder(pending[index])
            });
        }

        stopwatch.Stop();

        entries.Sort(static (a, b) =>
        {
            var bySize = b.SizeBytes.CompareTo(a.SizeBytes);
            return bySize != 0 ? bySize : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        largeFiles.Sort(static (a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        // Large files found deeper in the tree join the ones found at the top level.
        foreach (var nested in nestedLargeFiles)
        {
            if (largeFiles.Any(f => string.Equals(f.Path, nested.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            largeFiles.Add(new DiskEntry
            {
                Name = Path.GetFileName(nested.Path),
                Path = nested.Path,
                SizeBytes = nested.Length,
                IsDirectory = false,
                SizeStatus = ScanStatus.Complete,
                LastWriteTimeUtc = nested.LastWriteTimeUtc
            });
        }

        largeFiles.Sort(static (a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        return new DiskScanResult
        {
            Path = root,
            Status = partial ? ScanStatus.Partial : ScanStatus.Complete,
            TotalSizeBytes = totalBytes,
            TotalFileCount = totalFiles,
            Entries = entries,
            LargeFiles = largeFiles.Count > options.LargeFileLimit
                ? largeFiles.GetRange(0, options.LargeFileLimit)
                : largeFiles,
            UnreadablePaths = unreadable.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            TimedOut = timedOut,
            Duration = stopwatch.Elapsed
        };
    }

    private static bool IsInsightFolder(string path)
    {
        var name = Path.GetFileName(path);
        return InsightNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
