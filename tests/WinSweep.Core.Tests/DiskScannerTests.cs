using WinSweep.Core.Models;
using WinSweep.Core.Scanning;
using Xunit;

namespace WinSweep.Core.Tests;

public sealed class TempTree : IDisposable
{
    public string Root { get; }

    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string CreateFile(string relativePath, int sizeBytes)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var buffer = new byte[sizeBytes];
        File.WriteAllBytes(path, buffer);
        return path;
    }

    public string CreateFolder(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover test folder must never fail the suite.
        }
    }
}

public class DiskScannerTests
{
    [Fact]
    public void ReportsFolderSizesAndSortsLargestFirst()
    {
        using var tree = new TempTree();
        tree.CreateFile("big.bin", 300_000);
        tree.CreateFile("small.bin", 1_000);
        tree.CreateFolder("folder");
        tree.CreateFile(Path.Combine("folder", "medium.bin"), 100_000);

        var scanner = new DiskScanner();
        var result = scanner.Scan(tree.Root, new DiskScanOptions { LargeFileThresholdBytes = 10_000 });

        Assert.Equal(ScanStatus.Complete, result.Status);
        Assert.Equal(401_000, result.TotalSizeBytes);
        Assert.Equal(3, result.TotalFileCount);

        Assert.Equal("big.bin", result.Entries[0].Name);
        Assert.Equal("folder", result.Entries[1].Name);

        Assert.Contains(result.LargeFiles, f => f.Name == "big.bin");
        Assert.Contains(result.LargeFiles, f => f.Name == "medium.bin");
    }

    [Fact]
    public void ReportsAMissingFolderAsUnavailable()
    {
        var scanner = new DiskScanner();
        var missing = Path.Combine(Path.GetTempPath(), "winsweep-tests", "does-not-exist-" + Guid.NewGuid().ToString("N"));

        var result = scanner.Scan(missing);

        Assert.Equal(ScanStatus.Unavailable, result.Status);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void MeasuresAFolderWithoutInventingAZeroForAFile()
    {
        using var tree = new TempTree();
        var file = tree.CreateFile("data.bin", 4_096);

        var measurement = DirectorySizer.MeasureFile(file);

        Assert.Equal(4_096, measurement.Bytes);
        Assert.Equal(ScanStatus.Complete, measurement.Status);
    }

    [Fact]
    public void ReportsAnUnreadableFolderAsUnavailable()
    {
        var scanner = new DiskScanner();
        var missing = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));

        var measurement = DirectorySizer.Measure(missing);

        Assert.Equal(ScanStatus.Unavailable, measurement.Status);
        Assert.Equal(0, measurement.Bytes);
    }
}
