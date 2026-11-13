using WinSweep.Core.Logging;
using WinSweep.Core.Scanning;
using Xunit;

namespace WinSweep.Core.Tests;

public class DockerScannerTests
{
    private const string Df =
        "{\"Active\":\"2\",\"Reclaimable\":\"1.2GB (45%)\",\"Size\":\"2.6GB\",\"TotalCount\":\"5\",\"Type\":\"Images\"}\n" +
        "{\"Active\":\"1\",\"Reclaimable\":\"15MB (100%)\",\"Size\":\"15MB\",\"TotalCount\":\"4\",\"Type\":\"Containers\"}\n" +
        "{\"Active\":\"1\",\"Reclaimable\":\"3GB (90%)\",\"Size\":\"3.3GB\",\"TotalCount\":\"2\",\"Type\":\"Local Volumes\"}\n" +
        "{\"Active\":\"0\",\"Reclaimable\":\"800MB\",\"Size\":\"800MB\",\"TotalCount\":\"12\",\"Type\":\"Build Cache\"}\n";

    [Fact]
    public void ParsesImagesContainersAndBuildCacheButNeverVolumes()
    {
        var items = DockerScanner.Parse(Df, "abc123\r\ndef456\n\n");

        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i is { Kind: "images", Name: "Dangling images", Count: 2, Reclaimable: "" });
        Assert.Contains(items, i => i is { Kind: "containers", Name: "Stopped containers", Count: 3, Reclaimable: "15MB (100%)" });
        Assert.Contains(items, i => i is { Kind: "build-cache", Name: "Unused build cache", Count: 12, Reclaimable: "800MB" });
    }

    [Fact]
    public void NoDanglingIdsMeansNoImagesItem()
    {
        Assert.DoesNotContain(DockerScanner.Parse(Df, "\n"), i => i.Kind == "images");
    }

    [Fact]
    public void EmptyBuildCacheIsNotOffered()
    {
        var df = "{\"Active\":\"0\",\"Reclaimable\":\"0B (0%)\",\"Size\":\"0B\",\"TotalCount\":\"0\",\"Type\":\"Build Cache\"}";

        Assert.Empty(DockerScanner.Parse(df, ""));
    }

    [Fact]
    public void IgnoresGarbageAndEmptyInput()
    {
        Assert.Empty(DockerScanner.Parse("", ""));
        Assert.Empty(DockerScanner.Parse("not json\n{broken\n[1,2]\n42\n", "x"));
    }

    [Fact]
    public async Task UnknownKindRunsNothingAndWritesNothing()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"), "operations.jsonl");
        var log = new OperationLog(logPath);

        var result = await new DockerScanner().PruneAsync(new DockerItem("volumes", "Volumes", 1, ""), log, "abcd1234");

        Assert.Equal(WinSweep.Core.Maintenance.MaintenanceOutcome.Unavailable, result.Outcome);
        Assert.False(File.Exists(logPath));
    }
}
