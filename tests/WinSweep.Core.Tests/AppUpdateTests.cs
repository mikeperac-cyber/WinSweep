using WinSweep.Core.Apps;
using Xunit;

namespace WinSweep.Core.Tests;

public class AppUpdateTests
{
    private const string Sample =
        "Name            Id                      Version Available Source\r\n" +
        "----------------------------------------------------------------\r\n" +
        "Antigravity CLI Google.AntigravityCLI   1.5.6   1.5.7     winget\r\n" +
        "Cherry Studio   kangfenmao.CherryStudio 2.2.0   2.2.1     winget\r\n" +
        "Gemini          Google.GeminiDesktop    1.18.0  1.19.0    winget\r\n" +
        "Obsidian        Obsidian.Obsidian       1.15.0  1.15.1    winget\r\n" +
        "4 upgrades available.\r\n";

    [Fact]
    public void ReadsEveryRowOfTheTable()
    {
        var updates = AppUpdateService.ParseUpgradeTable(Sample);

        Assert.Equal(4, updates.Count);
        Assert.Equal(new AppUpdate("Antigravity CLI", "Google.AntigravityCLI", "1.5.6", "1.5.7"), updates[0]);
        Assert.Equal(new AppUpdate("Obsidian", "Obsidian.Obsidian", "1.15.0", "1.15.1"), updates[3]);
    }

    [Fact]
    public void IgnoresTheProgressSpinner()
    {
        var updates = AppUpdateService.ParseUpgradeTable("   - \r   | \r" + Sample);
        Assert.Equal(4, updates.Count);
    }

    [Fact]
    public void ReturnsNothingWhenThereIsNoTable()
    {
        Assert.Empty(AppUpdateService.ParseUpgradeTable("No installed package found matching input criteria.\r\n"));
        Assert.Empty(AppUpdateService.ParseUpgradeTable(string.Empty));
    }

    [Fact]
    public void StopsAtTheFirstBlankLine()
    {
        var output = Sample.Replace("4 upgrades available.\r\n", "\r\n") +
            "The following packages have an upgrade available, but require explicit targeting for upgrade:\r\n" +
            "Name   Id        Version Available Source\r\n" +
            "-----------------------------------------\r\n" +
            "Pinned Some.Pin  1.0     2.0       winget\r\n";

        var updates = AppUpdateService.ParseUpgradeTable(output);
        Assert.Equal(4, updates.Count);
        Assert.DoesNotContain(updates, u => u.Id == "Some.Pin");
    }

    [Fact]
    public void DropsRowsWhoseIdCouldInjectArguments()
    {
        var output =
            "Name  Id               Version Available\r\n" +
            "----------------------------------------\r\n" +
            "Evil  a&calc           1.0     2.0\r\n" +
            "Fine  Good.App         1.0     2.0\r\n";

        var updates = AppUpdateService.ParseUpgradeTable(output);
        Assert.Equal("Good.App", Assert.Single(updates).Id);
    }
}
