using WinSweep.Core.Models;
using WinSweep.Core.Services;
using WinSweep.Core.Sweep;
using Xunit;

namespace WinSweep.Core.Tests;

public class QuarantineVaultTests : IDisposable
{
    private const string Marker = "PLAINTEXT-MARKER-4f9c1e7a-do-not-leak";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winsweep-tests", Guid.NewGuid().ToString("N"));
    private readonly string _vaultRoot;

    public QuarantineVaultTests()
    {
        Directory.CreateDirectory(_root);
        _vaultRoot = Path.Combine(_root, "vault");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static RiskAnalyzer NoRisk() => new(Array.Empty<InstalledApp>(), Array.Empty<string>());

    private QuarantineVault NewVault() => new(_vaultRoot);

    private static SweepManifest Plan(params string[] paths) =>
        SweepPlanner.Build("clean", paths.Select(p => new SweepTarget(p, Path.GetFileName(p), "Temp", 1)), NoRisk(), dryRun: true);

    private string MakeTree()
    {
        var dir = Path.Combine(_root, "target");
        Directory.CreateDirectory(Path.Combine(dir, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(dir, "empty"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), Marker);
        File.WriteAllBytes(Path.Combine(dir, "sub", "b.bin"), Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray());
        File.WriteAllText(Path.Combine(dir, "sub", "deep", "c.txt"), "deep file");
        File.SetLastWriteTimeUtc(Path.Combine(dir, "a.txt"), new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(dir, "sub", "b.bin"), new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(dir, "sub", "deep", "c.txt"), new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234567));
        return dir;
    }

    private static string[] Listing(string dir) => Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
        .Select(p => $"{Path.GetRelativePath(dir, p)}|" + (File.Exists(p)
            ? Convert.ToHexString(File.ReadAllBytes(p)) + ":" + File.GetLastWriteTimeUtc(p).Ticks
            : "dir"))
        .OrderBy(s => s, StringComparer.Ordinal).ToArray();

    private string SweepFolder(SweepManifest m) => Path.Combine(_vaultRoot, m.Id);

    [Fact]
    public void QuarantineFolderRemovesOriginalsAndWritesArchive()
    {
        var dir = MakeTree();
        var session = NewVault().Begin(Plan(dir));

        var result = session.Quarantine(dir, 1);
        session.Complete();

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(dir));
        var onDisk = SweepManifest.Load(Path.Combine(SweepFolder(session.Manifest), "sweep-manifest.json"))!;
        var entry = onDisk.Entries.Single();
        Assert.False(onDisk.DryRun);
        Assert.Equal(SweepEntryStatus.Quarantined, entry.Status);
        Assert.Equal(64, entry.Sha256!.Length);
        Assert.Equal(3, entry.FilesArchived);
        Assert.False(string.IsNullOrEmpty(entry.AclSddl));
        Assert.True(File.Exists(Path.Combine(SweepFolder(session.Manifest), entry.Archive!)));
        Assert.EndsWith(".wsq", entry.Archive);
    }

    [Fact]
    public void RestoreBringsBackIdenticalBytesTimesAndEmptyFolders()
    {
        var dir = MakeTree();
        var before = Listing(dir);
        var vault = NewVault();
        var session = vault.Begin(Plan(dir));
        session.Quarantine(dir, 1);
        session.Complete();
        Assert.False(Directory.Exists(dir));

        var restored = vault.Restore(session.Manifest.Id);

        Assert.Equal(1, restored.Restored);
        Assert.Equal(0, restored.Failed);
        Assert.Empty(restored.Problems);
        Assert.Equal(before, Listing(dir));
        Assert.True(Directory.Exists(Path.Combine(dir, "empty")));
        Assert.Equal(SweepEntryStatus.Restored, vault.List().Single().Entries.Single().Status);
        Assert.Empty(Directory.GetFiles(SweepFolder(session.Manifest), "*.wsq"));
    }

    [Fact]
    public void SingleFileTargetRoundTrips()
    {
        var file = Path.Combine(_root, "single.log");
        File.WriteAllText(file, "just one file");
        var stamp = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);
        var vault = NewVault();
        var session = vault.Begin(Plan(file));

        var result = session.Quarantine(file, 13);
        session.Complete();
        Assert.True(result.Succeeded);
        Assert.False(File.Exists(file));

        Assert.Equal(1, vault.Restore(session.Manifest.Id).Restored);
        Assert.Equal("just one file", File.ReadAllText(file));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ArchiveIsEncrypted()
    {
        var dir = MakeTree();
        var session = NewVault().Begin(Plan(dir));
        session.Quarantine(dir, 1);

        var bytes = File.ReadAllBytes(Path.Combine(SweepFolder(session.Manifest), session.Manifest.Entries[0].Archive!));

        Assert.True(bytes.Length > 16);
        Assert.Equal(-1, System.Text.Encoding.ASCII.GetString(bytes).IndexOf(Marker, StringComparison.Ordinal));
        Assert.Equal(-1, System.Text.Encoding.Unicode.GetString(bytes).IndexOf(Marker, StringComparison.Ordinal));
    }

    [Fact]
    public void TamperedArchiveIsNotRestored()
    {
        var dir = MakeTree();
        var vault = NewVault();
        var session = vault.Begin(Plan(dir));
        session.Quarantine(dir, 1);
        session.Complete();
        var archive = Path.Combine(SweepFolder(session.Manifest), session.Manifest.Entries[0].Archive!);
        var bytes = File.ReadAllBytes(archive);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);

        var result = vault.Restore(session.Manifest.Id);

        Assert.Equal(0, result.Restored);
        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Problems, p => p.Contains("checksum mismatch, not restored"));
        Assert.False(Directory.Exists(dir));
        Assert.Equal(SweepEntryStatus.Quarantined, vault.List().Single().Entries.Single().Status);
    }

    [Fact]
    public void LockedFileIsSkippedAndSiblingsAreQuarantined()
    {
        var dir = MakeTree();
        var locked = Path.Combine(dir, "a.txt");
        var session = NewVault().Begin(Plan(dir));

        RemovalResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = session.Quarantine(dir, 1);
        }

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(Path.Combine(dir, "sub", "b.bin")));
        var entry = session.Manifest.Entries.Single();
        Assert.Equal(SweepEntryStatus.Quarantined, entry.Status);
        Assert.Equal(2, entry.FilesArchived);
        Assert.Contains(locked, entry.SkippedFiles);
    }

    [Fact]
    public void FolderWithOnlyLockedFileFailsAndLeavesEverything()
    {
        var dir = Path.Combine(_root, "lockedonly");
        Directory.CreateDirectory(dir);
        var locked = Path.Combine(dir, "x.bin");
        File.WriteAllText(locked, "x");
        var session = NewVault().Begin(Plan(dir));

        RemovalResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = session.Quarantine(dir, 1);
        }

        Assert.False(result.Succeeded);
        Assert.Equal("Every file is in use", result.FailureReason);
        Assert.True(File.Exists(locked));
        Assert.Empty(Directory.GetFiles(SweepFolder(session.Manifest), "*.wsq"));
        Assert.Equal(SweepEntryStatus.Skipped, session.Manifest.Entries.Single().Status);
    }

    [Fact]
    public void PathsOutsideTheManifestOrTheGuardAreRefused()
    {
        var dir = MakeTree();
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "f"), "f");
        var session = NewVault().Begin(Plan(dir));

        var notPlanned = session.Quarantine(other, 1);
        var driveRoot = session.Quarantine(Path.GetPathRoot(_root)!, 1);

        Assert.False(notPlanned.Succeeded);
        Assert.Equal("Not part of this sweep", notPlanned.FailureReason);
        Assert.True(File.Exists(Path.Combine(other, "f")));
        Assert.False(driveRoot.Succeeded);
        Assert.Equal("Refused by the safety guard", driveRoot.FailureReason);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void RestoreDoesNotOverwriteARecreatedFile()
    {
        var dir = MakeTree();
        var vault = NewVault();
        var session = vault.Begin(Plan(dir));
        session.Quarantine(dir, 1);
        session.Complete();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "newer content");

        var result = vault.Restore(session.Manifest.Id);

        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.SkippedExisting);
        Assert.Equal("newer content", File.ReadAllText(Path.Combine(dir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "sub", "b.bin")));
    }

    [Fact]
    public void PurgeExpiredRemovesOnlyOldSweeps()
    {
        var now = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        var vault = NewVault();
        vault.Begin(new SweepManifest { Id = "00000001", Source = "clean", CreatedAt = now.AddDays(-8) });
        vault.Begin(new SweepManifest { Id = "00000002", Source = "clean", CreatedAt = now.AddDays(-6) });

        var removed = vault.PurgeExpired(TimeSpan.FromDays(7), now);

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "00000002" }, vault.List().Select(m => m.Id));
        Assert.True(File.Exists(Path.Combine(_vaultRoot, "vault.key")));
    }

    [Fact]
    public void BadSweepIdsNeverTouchTheDisk()
    {
        var vault = NewVault();
        var sibling = Path.Combine(_root, "x");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep"), "keep");
        Directory.CreateDirectory(_vaultRoot);

        var restore = vault.Restore("../x");

        Assert.Equal(0, restore.Restored);
        Assert.Equal(new[] { "That sweep does not exist." }, restore.Problems);
        Assert.False(vault.Delete(".."));
        Assert.False(vault.Delete("../x"));
        Assert.True(File.Exists(Path.Combine(sibling, "keep")));
        Assert.True(Directory.Exists(_vaultRoot));
    }

    [Fact]
    public void ListIsNewestFirstAndSkipsFoldersWithoutManifest()
    {
        var vault = NewVault();
        vault.Begin(new SweepManifest { Id = "0000000a", Source = "clean", CreatedAt = DateTimeOffset.Now.AddDays(-2) });
        vault.Begin(new SweepManifest { Id = "0000000b", Source = "clean", CreatedAt = DateTimeOffset.Now.AddDays(-1) });
        Directory.CreateDirectory(Path.Combine(_vaultRoot, "junkfolder"));

        Assert.Equal(new[] { "0000000b", "0000000a" }, vault.List().Select(m => m.Id));
    }

    [Fact]
    public void ParentOfTheVaultCannotBeQuarantined()
    {
        var session = NewVault().Begin(Plan(_root));
        File.WriteAllText(Path.Combine(_root, "f.txt"), "f");

        var result = session.Quarantine(_root, 1);

        Assert.False(result.Succeeded);
        Assert.True(Directory.Exists(_vaultRoot));
        Assert.True(File.Exists(Path.Combine(_root, "f.txt")));
    }
}
