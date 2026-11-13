using WinSweep.Core.Scanning;
using Xunit;

namespace WinSweep.Core.Tests;

public class ProjectActivityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));

    public ProjectActivityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Write(string relative, DateTime lastWriteUtc)
    {
        var file = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, lastWriteUtc);
        return file;
    }

    [Fact]
    public void LatestSourceWriteReturnsTheNewestSourceAndIgnoresBuildOutput()
    {
        var old = DateTime.UtcNow.AddDays(-40);
        var newest = DateTime.UtcNow.AddDays(-20);
        Write(@"src\a.ts", old);
        Write(@"src\b.cs", newest);
        Write(@"node_modules\pkg\index.js", DateTime.UtcNow);
        Write(@"bin\Debug\app.json", DateTime.UtcNow);
        Write(@".git\config.yml", DateTime.UtcNow);

        var latest = ProjectActivity.LatestSourceWriteUtc(_root);

        Assert.NotNull(latest);
        Assert.Equal(newest, latest!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void LatestSourceWriteIgnoresNonSourceFiles()
    {
        var old = DateTime.UtcNow.AddDays(-40);
        Write("main.py", old);
        Write("notes.docx", DateTime.UtcNow);

        Assert.Equal(old, ProjectActivity.LatestSourceWriteUtc(_root)!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void NoSourceFilesAndNoRepoMeansUnknownActivity()
    {
        Write("notes.docx", DateTime.UtcNow);

        Assert.Null(ProjectActivity.LatestSourceWriteUtc(_root));
        Assert.Null(ProjectActivity.LastCommitUtc(_root));
        Assert.Null(ProjectActivity.LastActivityUtc(_root));
    }
}
