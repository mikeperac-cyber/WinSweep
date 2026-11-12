using WinSweep.Core.Models;
using Xunit;

namespace WinSweep.Core.Tests;

public class ByteSizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024L * 1024, "1 MB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    public void FormatsBytesTheWayWindowsDoes(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSizeFormatter.Format(bytes));
    }

    [Fact]
    public void DoesNotRoundUpIntoTheNextUnit()
    {
        // 999,999 bytes is under a megabyte and must not read as "1000.0 KB".
        var formatted = ByteSizeFormatter.Format(999_999);
        Assert.Equal("976.6 KB", formatted);
    }

    [Fact]
    public void KeepsUnknownSizesUnknown()
    {
        Assert.Equal("Unknown", ByteSizeFormatter.Format(-1));
    }
}
