using WinSweep.Core.Safety;
using Xunit;

namespace WinSweep.Core.Tests;

public class PathGuardTests
{
    [Fact]
    public void RefusesADriveRoot()
    {
        var verdict = PathGuard.Evaluate("C:\\");
        Assert.False(verdict.Allowed);
        Assert.Equal("Filesystem root", verdict.Reason);
    }

    [Fact]
    public void RefusesTheWindowsFolder()
    {
        var verdict = PathGuard.Evaluate(@"C:\Windows\System32\drivers");
        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void RefusesProgramFiles()
    {
        var verdict = PathGuard.Evaluate(@"C:\Program Files\Some App");
        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void RefusesTheUserProfileRoot()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var verdict = PathGuard.Evaluate(profile);
        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void AllowsAFolderInsideTheUserProfile()
    {
        // The temp folder is a normal cleanup target and must not be blocked by the
        // protected-location rules.
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp");

        var verdict = PathGuard.Evaluate(path);
        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void RefusesProgramDataSubfolders()
    {
        var verdict = PathGuard.Evaluate(@"C:\ProgramData\Some App\cache");
        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void DetectsPersonalData()
    {
        Assert.True(PathGuard.LooksLikePersonalData(@"C:\Users\Someone\Documents\notes.docx"));
        Assert.True(PathGuard.LooksLikePersonalData(@"C:\Users\Someone\Downloads\setup.exe"));
        Assert.False(PathGuard.LooksLikePersonalData(@"C:\Users\Someone\AppData\Local\Temp\cache.tmp"));
    }

    [Fact]
    public void ComparesPathsCaseInsensitively()
    {
        Assert.True(PathGuard.IsSameOrUnder(@"C:\Users\Me\AppData", @"c:\users\me"));
        Assert.False(PathGuard.IsSameOrUnder(@"C:\Users\Other", @"C:\Users\Me"));
    }
}
