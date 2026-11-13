using WinSweep.Core.Cleanup;
using WinSweep.Core.Logging;
using WinSweep.Core.Models;
using WinSweep.Core.Scanning;
using WinSweep.Core.Services;
using WinSweep.Core.Settings;
using Xunit;

namespace WinSweep.Core.Tests;

public sealed class ArtifactTree : IDisposable
{
    public string Root { get; }
    public string Project { get; }

    public ArtifactTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "winsweep-artifact-tests", Guid.NewGuid().ToString("N"));
        Project = Path.Combine(Root, "ExampleProject");
        Directory.CreateDirectory(Project);
        var marker = Path.Combine(Project, "package.json");
        File.WriteAllText(marker, "{\"name\":\"fixture\"}");
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddDays(-90)); // An idle project, so inactivity does not hold fixtures back.
    }

    public string AddSource(string relative, DateTime lastWriteUtc)
    {
        var file = Path.Combine(Project, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, lastWriteUtc);
        return file;
    }

    public string CreateArtifact(string name, DateTime lastActivityUtc)
    {
        var artifact = Path.Combine(Project, name);
        var nested = Path.Combine(artifact, "nested");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "output.bin");
        File.WriteAllBytes(file, new byte[4096]);
        File.SetLastWriteTimeUtc(file, lastActivityUtc);

        foreach (var directory in Directory.GetDirectories(artifact, "*", SearchOption.AllDirectories)
                     .Prepend(artifact))
        {
            Directory.SetLastWriteTimeUtc(directory, lastActivityUtc);
        }

        return artifact;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal); // Git object files are read-only.
                }

                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test residue must not fail unrelated cases.
        }
    }
}

public class ProjectArtifactScannerTests
{
    [Fact]
    public void FindsOldBuildArtifactsAndSelectsOnlyVerifiedCandidates()
    {
        using var tree = new ArtifactTree();
        tree.CreateArtifact("node_modules", DateTime.UtcNow.AddDays(-10));
        var scanner = new ProjectArtifactScanner(
            scanBudget: TimeSpan.FromSeconds(5),
            artifactBudget: TimeSpan.FromSeconds(2),
            minimumAge: TimeSpan.FromDays(7));

        var result = scanner.Scan(new[] { tree.Root });

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("node_modules", artifact.Name);
        Assert.Equal(4096, artifact.SizeBytes);
        Assert.Equal(ScanStatus.Complete, artifact.SizeStatus);
        Assert.True(artifact.IsSelected);
        Assert.Null(artifact.BlockedReason);
    }

    [Fact]
    public void HoldsAnArtifactWithRecentChanges()
    {
        using var tree = new ArtifactTree();
        tree.CreateArtifact("dist", DateTime.UtcNow);
        var scanner = new ProjectArtifactScanner(
            scanBudget: TimeSpan.FromSeconds(5),
            artifactBudget: TimeSpan.FromSeconds(2),
            minimumAge: TimeSpan.FromDays(7));

        var result = scanner.Scan(new[] { tree.Root });

        var artifact = Assert.Single(result.Artifacts);
        Assert.False(artifact.IsSelected);
        Assert.Contains("Changed within", artifact.BlockedReason);
    }

    [Fact]
    public void HoldsAnArtifactThatContainsCredentials()
    {
        using var tree = new ArtifactTree();
        var artifactPath = tree.CreateArtifact("build", DateTime.UtcNow.AddDays(-10));
        File.WriteAllText(Path.Combine(artifactPath, ".env"), "SECRET=fixture");
        File.SetLastWriteTimeUtc(Path.Combine(artifactPath, ".env"), DateTime.UtcNow.AddDays(-10));
        Directory.SetLastWriteTimeUtc(artifactPath, DateTime.UtcNow.AddDays(-10));

        var result = new ProjectArtifactScanner(
            scanBudget: TimeSpan.FromSeconds(5),
            artifactBudget: TimeSpan.FromSeconds(2),
            minimumAge: TimeSpan.FromDays(7)).Scan(new[] { tree.Root });

        var artifact = Assert.Single(result.Artifacts);
        Assert.False(artifact.IsSelected);
        Assert.Contains("credentials", artifact.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HoldsGitArtifactsWhenTrackedFileStatusCannotBeVerified()
    {
        using var tree = new ArtifactTree();
        Directory.CreateDirectory(Path.Combine(tree.Project, ".git"));
        tree.CreateArtifact("target", DateTime.UtcNow.AddDays(-10));
        var scanner = new ProjectArtifactScanner(
            scanBudget: TimeSpan.FromSeconds(5),
            artifactBudget: TimeSpan.FromSeconds(2),
            minimumAge: TimeSpan.FromDays(7));

        var result = scanner.Scan(new[] { tree.Root });

        var artifact = Assert.Single(result.Artifacts);
        Assert.False(artifact.IsSelected);
        Assert.Contains("Git", artifact.BlockedReason);
    }

    private static ProjectArtifactScanner InactivityScanner(TimeSpan inactivity) => new(
        scanBudget: TimeSpan.FromSeconds(5),
        artifactBudget: TimeSpan.FromSeconds(2),
        minimumAge: TimeSpan.FromDays(7),
        projectInactivity: inactivity);

    [Fact]
    public void HoldsBuildOutputOfAProjectWorkedOnRecentlyAndHonoursTheThreshold()
    {
        using var tree = new ArtifactTree();
        tree.CreateArtifact("node_modules", DateTime.UtcNow.AddDays(-60));
        tree.AddSource(@"src\index.ts", DateTime.UtcNow.AddDays(-10));

        var held = Assert.Single(InactivityScanner(TimeSpan.FromDays(30)).Scan(new[] { tree.Root }).Artifacts);
        Assert.False(held.IsSelected);
        Assert.Contains("worked on within the last 30 days", held.BlockedReason);
        Assert.NotNull(held.ProjectLastActivityUtc);
        Assert.StartsWith("Project last worked on", held.ActivityDisplay);

        tree.AddSource(@"src\index.ts", DateTime.UtcNow.AddDays(-45));
        var free = Assert.Single(InactivityScanner(TimeSpan.FromDays(30)).Scan(new[] { tree.Root }).Artifacts);
        Assert.True(free.IsSelected);
        Assert.Null(free.BlockedReason);

        var heldAgain = Assert.Single(InactivityScanner(TimeSpan.FromDays(60)).Scan(new[] { tree.Root }).Artifacts);
        Assert.False(heldAgain.IsSelected);
        Assert.Contains("worked on within the last 60 days", heldAgain.BlockedReason);
    }

    [Fact]
    public void HoldsBuildOutputWhenProjectActivityCannotBeMeasured()
    {
        using var tree = new ArtifactTree();
        File.Delete(Path.Combine(tree.Project, "package.json"));
        File.WriteAllText(Path.Combine(tree.Project, "App.vbproj"), "<Project />"); // A marker, but not a measured source file.
        tree.CreateArtifact("node_modules", DateTime.UtcNow.AddDays(-60));

        var artifact = Assert.Single(InactivityScanner(TimeSpan.FromDays(30)).Scan(new[] { tree.Root }).Artifacts);

        Assert.False(artifact.IsSelected);
        Assert.Equal("This project's recent activity could not be verified, so its build output is kept.", artifact.BlockedReason);
    }

    [Fact]
    public void HoldsBuildOutputWhenTheLastGitCommitIsRecent()
    {
        if (RunGit(Path.GetTempPath(), "--version") != 0)
        {
            return; // Git is not available on this machine.
        }

        using var tree = new ArtifactTree();
        tree.CreateArtifact("node_modules", DateTime.UtcNow.AddDays(-60));
        Assert.Equal(0, RunGit(tree.Project, "init", "-q"));
        Assert.Equal(0, RunGit(tree.Project, "add", "package.json"));
        Assert.Equal(0, RunGit(tree.Project, "-c", "user.name=Test", "-c", "user.email=test@example.com",
            "-c", "commit.gpgsign=false", "commit", "-q", "-m", "fixture"));
        File.SetLastWriteTimeUtc(Path.Combine(tree.Project, "package.json"), DateTime.UtcNow.AddDays(-90));

        var artifact = Assert.Single(InactivityScanner(TimeSpan.FromDays(30)).Scan(new[] { tree.Root }).Artifacts);

        Assert.False(artifact.IsSelected);
        Assert.Contains("worked on within the last 30 days", artifact.BlockedReason);
    }

    private static int RunGit(string workingDirectory, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git.exe")
        {
            WorkingDirectory = workingDirectory,
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
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

    [Fact]
    public void DoesNotScanThroughAUserSelectedJunction()
    {
        using var tree = new ArtifactTree();
        var linkRoot = Path.Combine(tree.Root, "linked-projects");
        Directory.CreateDirectory(linkRoot);

        var link = Path.Combine(linkRoot, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, tree.Project);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Symlink creation can require developer mode on Windows.
        }

        var result = new ProjectArtifactScanner().Scan(new[] { linkRoot });

        Assert.Empty(result.Artifacts);
    }
}

public class ProjectArtifactServiceTests
{
    [Fact]
    public void RevalidatesAndMovesSelectedArtifactsThroughTheRecycleDelegate()
    {
        using var tree = new ArtifactTree();
        var artifactPath = tree.CreateArtifact("build", DateTime.UtcNow.AddDays(-10));
        var artifact = new ProjectArtifact
        {
            ProjectName = Path.GetFileName(tree.Project),
            ProjectPath = tree.Project,
            Name = "build",
            Path = artifactPath,
            Description = "Generated build output.",
            SizeBytes = 4096,
            SizeStatus = ScanStatus.Complete,
            LastActivityUtc = DateTime.UtcNow.AddDays(-10),
            IsSelected = true
        };

        var called = false;
        var service = new ProjectArtifactService(
            new OperationLog(Path.Combine(tree.Root, "operations.jsonl")),
            new AppSettings(),
            (path, size) =>
            {
                called = true;
                Assert.Equal(artifactPath, path);
                Assert.Equal(4096, size);
                return RemovalResult.Ok(size);
            });

        var result = service.Execute(new[] { artifact }, "test-run");

        Assert.True(called);
        Assert.Equal(1, result.RecycledCount);
        Assert.Equal(4096, result.BytesReclaimed);
        Assert.True(Directory.Exists(artifactPath)); // The test delegate did not delete anything.
    }

    [Fact]
    public void KeepsUnselectedArtifactsWithoutCallingTheRemover()
    {
        using var tree = new ArtifactTree();
        var artifactPath = tree.CreateArtifact("dist", DateTime.UtcNow.AddDays(-10));
        var artifact = new ProjectArtifact
        {
            ProjectName = Path.GetFileName(tree.Project),
            ProjectPath = tree.Project,
            Name = "dist",
            Path = artifactPath,
            Description = "Generated distribution output.",
            SizeBytes = 4096,
            SizeStatus = ScanStatus.Complete,
            IsSelected = false
        };

        var called = false;
        var service = new ProjectArtifactService(
            new OperationLog(Path.Combine(tree.Root, "operations.jsonl")),
            new AppSettings(),
            (_, _) =>
            {
                called = true;
                return RemovalResult.Ok(4096);
            });

        var result = service.Execute(new[] { artifact }, "test-run");

        Assert.False(called);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(Directory.Exists(artifactPath));
    }
}
