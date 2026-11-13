using WinSweep.Core.Scanning;
using Xunit;

namespace WinSweep.Core.Tests;

public class ProjectActivityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));

    public ProjectActivityTests() => Directory.CreateDirectory(_root);

    private readonly List<string> _denied = new();

    public void Dispose()
    {
        ArtifactTree.RestoreListing(_denied);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // Git object files are read-only.
            }

            Directory.Delete(_root, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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
    public void LatestSourceWriteCountsAnyFileIncludingPackagesFolders()
    {
        Write("main.py", DateTime.UtcNow.AddDays(-40));
        Write("notes.docx", DateTime.UtcNow.AddDays(-10));
        var newest = DateTime.UtcNow.AddDays(-5);
        Write(@"packages\lib\a.ex", newest);

        Assert.Equal(newest, ProjectActivity.LatestSourceWriteUtc(_root)!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void LatestSourceWriteCountsADocumentWhenItIsTheNewestFile()
    {
        Write("main.py", DateTime.UtcNow.AddDays(-40));
        var docx = DateTime.UtcNow.AddDays(-10);
        Write("notes.docx", docx);

        Assert.Equal(docx, ProjectActivity.LatestSourceWriteUtc(_root)!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AnEmptyProjectWithNoRepoMeansUnknownActivity()
    {
        Assert.Null(ProjectActivity.LatestSourceWriteUtc(_root));
        Assert.Null(ProjectActivity.LastCommitUtc(_root));
        Assert.Null(ProjectActivity.LastActivityUtc(_root));
    }

    [Fact]
    public void RunningOutOfFileOrTimeBudgetMeansUnknown()
    {
        Write("a.ts", DateTime.UtcNow.AddDays(-40));
        Write("b.ts", DateTime.UtcNow.AddDays(-41));

        Assert.NotNull(ProjectActivity.LatestSourceWriteUtc(_root));
        Assert.Null(ProjectActivity.LatestSourceWriteUtc(_root, maxFiles: 1));
        Assert.Null(ProjectActivity.LatestSourceWriteUtc(_root, budget: TimeSpan.Zero));
    }

    [Fact]
    public void AFolderThatCannotBeListedMeansUnknown()
    {
        Write("a.ts", DateTime.UtcNow.AddDays(-40));
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        if (!ArtifactTree.TryDenyListing(locked, _denied))
        {
            return; // The ACL could not be set on this machine.
        }

        Assert.Null(ProjectActivity.LatestSourceWriteUtc(_root));
        Assert.Null(ProjectActivity.LastActivityUtc(_root));
    }

    [Fact]
    public void AGitRepoWithNoCommitsIsJudgedByItsFiles()
    {
        var old = DateTime.UtcNow.AddDays(-90);
        Write("main.py", old);
        if (!Git("--version") || !Git("init", "-q"))
        {
            return; // Git is not available on this machine.
        }

        Assert.Null(ProjectActivity.LastCommitUtc(_root));
        Assert.Equal(old, ProjectActivity.LastActivityUtc(_root)!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AGitRepoWhoseHistoryCannotBeReadIsUnknown()
    {
        Write("main.py", DateTime.UtcNow.AddDays(-90));
        Directory.CreateDirectory(Path.Combine(_root, ".git")); // Not a valid repository.

        Assert.Null(ProjectActivity.LastActivityUtc(_root));
    }

    private bool Git(params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git.exe")
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            _ = output.Result;
            _ = error.Result;
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
