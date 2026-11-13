using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using WinSweep.Core.Safety;
using WinSweep.Core.Services;

namespace WinSweep.Core.Sweep;

/// <summary>What a restore did. Problems holds one line per thing that did not go back.</summary>
public sealed record RestoreResult(int Restored, int Failed, int SkippedExisting, IReadOnlyList<string> Problems);

/// <summary>
/// Holds swept items as encrypted archives so a sweep can be undone. An item is archived, the archive
/// is checksummed, and only then is the original removed.
/// </summary>
public sealed class QuarantineVault
{
    private const string ManifestFileName = "sweep-manifest.json";
    private const string KeyFileName = "vault.key";
    private static readonly Regex IdPattern = new(@"^[0-9a-f]{8}\z", RegexOptions.Compiled);

    /// <summary>Folder that holds one sub-folder per sweep, plus vault.key.</summary>
    public string Root { get; }

    public QuarantineVault(string? root = null)
    {
        Root = PathGuard.Normalize(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSweep", "Vault"));
    }

    /// <summary>
    /// Starts a real sweep: marks the manifest as not a dry run, creates its folder and saves it.
    /// Throws an IOException or UnauthorizedAccessException when the vault folder cannot be written,
    /// and an InvalidOperationException when a sweep with the same id is already in the vault.
    /// </summary>
    public SweepSession Begin(SweepManifest manifest)
    {
        if (!IdPattern.IsMatch(manifest.Id))
        {
            throw new ArgumentException("The sweep id must be 8 lowercase hex characters.", nameof(manifest));
        }

        var folder = Path.Combine(Root, manifest.Id);
        if (File.Exists(ManifestFile(folder)))
        {
            throw new InvalidOperationException($"A sweep with id {manifest.Id} already exists in the vault.");
        }

        manifest.DryRun = false;
        Directory.CreateDirectory(folder);
        var session = new SweepSession(this, manifest, folder, LoadKey(createIfMissing: true));
        manifest.Save(Path.Combine(folder, ManifestFileName));
        return session;
    }

    /// <summary>Every sweep with a readable manifest, newest first.</summary>
    public IReadOnlyList<SweepManifest> List()
    {
        var manifests = new List<SweepManifest>();
        foreach (var folder in SweepFolders())
        {
            var manifest = SweepManifest.Load(Path.Combine(folder, ManifestFileName));
            if (manifest is not null)
            {
                manifests.Add(manifest);
            }
        }

        return manifests.OrderByDescending(m => m.CreatedAt).ToList();
    }

    /// <summary>Puts every quarantined entry of a sweep back where it came from. Existing files are never overwritten.</summary>
    public RestoreResult Restore(string sweepId, CancellationToken cancellationToken = default)
    {
        var manifestPath = ManifestPathFor(sweepId);
        var manifest = manifestPath is null ? null : SweepManifest.Load(manifestPath);
        if (manifestPath is null || manifest is null)
        {
            return new RestoreResult(0, 0, 0, new[] { "That sweep does not exist." });
        }

        var folder = Path.GetDirectoryName(manifestPath)!;
        var key = LoadKey(createIfMissing: false);
        var problems = new List<string>();
        int restored = 0, failed = 0, skippedExisting = 0;

        try
        {
            foreach (var entry in manifest.Entries.Where(e => e.Status == SweepEntryStatus.Quarantined))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? problem;
                try
                {
                    problem = RestoreEntry(entry, folder, key, cancellationToken, ref skippedExisting, problems);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // One damaged manifest entry must not stop the others from coming back.
                    problem = e.Message;
                }

                if (problem is null)
                {
                    restored++;
                    continue;
                }

                failed++;
                entry.Detail = problem;
                problems.Add($"{entry.Path}: {problem}");
            }
        }
        finally
        {
            try
            {
                manifest.Save(manifestPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"The sweep record could not be updated: {e.Message}");
            }
        }

        return new RestoreResult(restored, failed, skippedExisting, problems);
    }

    /// <summary>Permanently removes one sweep folder. Returns false when it does not exist or cannot be removed.</summary>
    public bool Delete(string sweepId)
    {
        var manifestPath = ManifestPathFor(sweepId, requireManifest: false);
        if (manifestPath is null)
        {
            return false;
        }

        var folder = Path.GetDirectoryName(manifestPath)!;
        if (!Directory.Exists(folder) || PathGuard.IsReparsePoint(folder))
        {
            return false;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes sweeps older than <paramref name="maxAge"/> by their creation time. Returns how many went.</summary>
    public int PurgeExpired(TimeSpan maxAge, DateTimeOffset? now = null)
    {
        var cutoff = (now ?? DateTimeOffset.Now) - maxAge;
        var removed = 0;
        foreach (var manifest in List())
        {
            if (manifest.CreatedAt < cutoff && Delete(manifest.Id))
            {
                removed++;
            }
        }

        return removed;
    }

    private string? RestoreEntry(SweepEntry entry, string folder, byte[]? key, CancellationToken ct,
        ref int skippedExisting, List<string> problems)
    {
        if (key is null)
        {
            return "The vault key is missing or unreadable";
        }

        // The manifest is data on disk: it must name a plain .wsq file inside the sweep folder.
        if (entry.Archive is null || entry.Archive != Path.GetFileName(entry.Archive) ||
            !entry.Archive.EndsWith(".wsq", StringComparison.OrdinalIgnoreCase))
        {
            return "invalid archive name in the sweep record, not restored";
        }

        var archive = Path.Combine(folder, entry.Archive);
        if (!File.Exists(archive))
        {
            return "checksum mismatch, not restored";
        }

        int leftAlone;
        try
        {
            if (!string.Equals(SweepArchive.Sha256Hex(archive), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return "checksum mismatch, not restored";
            }

            var verdict = PathGuard.Evaluate(entry.Path);
            if (!verdict.Allowed)
            {
                return $"Refused by the safety guard ({verdict.Reason})";
            }

            (_, leftAlone) = SweepArchive.Extract(archive, entry.Path, entry.IsDirectory, key, ct);
            skippedExisting += leftAlone;
        }
        catch (Exception e) when (e is InvalidDataException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }

        if (entry.AclSddl is not null && leftAlone == 0)
        {
            try
            {
                ApplyAcl(entry);
            }
            catch (Exception e)
            {
                // ACL APIs throw many unrelated types for a bad SDDL or an unmappable account; the files are back regardless.
                problems.Add($"{entry.Path}: restored, but the permissions could not be re-applied ({e.Message})");
            }
        }

        if (leftAlone > 0)
        {
            // Not everything came back, so keep the archive: it is still the only copy of those files.
            entry.Detail = $"{leftAlone} file(s) already existed and were left alone";
            return null;
        }

        entry.Status = SweepEntryStatus.Restored;
        entry.Detail = string.Empty;
        try
        {
            File.Delete(archive);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The restore itself worked; a leftover archive is harmless.
        }

        // A permissions problem is reported but is not a failed restore.
        return null;
    }

    private static void ApplyAcl(SweepEntry entry)
    {
        if (entry.IsDirectory)
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(entry.AclSddl!, AccessControlSections.Access);
            new DirectoryInfo(entry.Path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(entry.AclSddl!, AccessControlSections.Access);
            new FileInfo(entry.Path).SetAccessControl(security);
        }
    }

    private IEnumerable<string> SweepFolders()
    {
        if (!Directory.Exists(Root))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateDirectories(Root)
                .Where(d => IdPattern.IsMatch(Path.GetFileName(d))).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Manifest path for a valid id (folder directly under Root), or null for anything else.</summary>
    private string? ManifestPathFor(string sweepId, bool requireManifest = true)
    {
        if (!IdPattern.IsMatch(sweepId))
        {
            return null;
        }

        var folder = Path.Combine(Root, sweepId);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(folder)), Root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = Path.Combine(folder, ManifestFileName);
        return requireManifest && !File.Exists(path) ? null : path;
    }

    /// <summary>
    /// Reads the DPAPI-protected key. A missing file is created when asked to; an existing file that
    /// cannot be read is never replaced, because that would orphan every archive made with it.
    /// </summary>
    private byte[]? LoadKey(bool createIfMissing)
    {
        var path = Path.Combine(Root, KeyFileName);
        try
        {
            if (File.Exists(path))
            {
                var key = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                return key.Length == 32 ? key : null;
            }

            if (!createIfMissing)
            {
                return null;
            }

            Directory.CreateDirectory(Root);
            var fresh = RandomNumberGenerator.GetBytes(32);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, ProtectedData.Protect(fresh, null, DataProtectionScope.CurrentUser));
            File.Move(temp, path);
            return fresh;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    internal static string ManifestFile(string sweepFolder) => Path.Combine(sweepFolder, ManifestFileName);
}

/// <summary>One real sweep in progress. Quarantine every target through it, then call <see cref="Complete"/>.</summary>
public sealed class SweepSession
{
    private readonly string _folder;
    private readonly byte[]? _key;
    private readonly string _vaultRoot;

    public SweepManifest Manifest { get; }

    /// <summary>Test seam: runs after the index is saved and before any original is deleted.</summary>
    internal Action? BeforeDelete { get; set; }

    internal SweepSession(QuarantineVault vault, SweepManifest manifest, string folder, byte[]? key)
    {
        _vaultRoot = vault.Root;
        Manifest = manifest;
        _folder = folder;
        _key = key;
    }

    /// <summary>Archives, verifies and then removes one target. Same shape as RecycleBin.Send so it can be a remover.</summary>
    public RemovalResult Quarantine(string path, long sizeBytes)
    {
        var index = FindEntry(path);
        var result = QuarantineCore(path, index);
        if (index >= 0)
        {
            try
            {
                Manifest.Save(QuarantineVault.ManifestFile(_folder));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best-effort checkpoint; Complete() saves again and reports a persistent failure.
            }
        }

        return result;
    }

    /// <summary>Saves the manifest. Throws an IOException or UnauthorizedAccessException when it cannot be written.</summary>
    public void Complete() => Manifest.Save(QuarantineVault.ManifestFile(_folder));

    private RemovalResult QuarantineCore(string path, int index)
    {
        if (index >= 0 && Manifest.Entries[index].Status is SweepEntryStatus.Quarantined or SweepEntryStatus.Restored)
        {
            // Checked first and left untouched: restoration relies on this entry status.
            return RemovalResult.Failed("Already swept");
        }

        if (!PathGuard.Evaluate(path).Allowed)
        {
            return Fail(index, "Refused by the safety guard");
        }

        var full = PathGuard.Normalize(path);
        if (PathGuard.IsSameOrUnder(full, _vaultRoot) || PathGuard.IsSameOrUnder(_vaultRoot, full))
        {
            return Fail(index, "Refused: it is, or contains, the quarantine vault");
        }

        if (index < 0)
        {
            return Fail(index, "Not part of this sweep");
        }

        var entry = Manifest.Entries[index];
        if (!(entry.IsDirectory ? Directory.Exists(full) : File.Exists(full)))
        {
            return Fail(index, "The item no longer exists");
        }

        if (_key is null)
        {
            return Fail(index, "The vault key is missing or unreadable");
        }

        try
        {
            entry.AclSddl = entry.IsDirectory
                ? new DirectoryInfo(full).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access)
                : new FileInfo(full).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            entry.AclSddl = null;
        }

        var archiveName = $"{index:D4}.wsq";
        var archivePath = Path.Combine(_folder, archiveName);
        if (File.Exists(archivePath))
        {
            // Not ours to overwrite or delete.
            return Fail(index, "An archive with that name already exists");
        }

        ArchiveResult archive;
        string sha;
        try
        {
            archive = SweepArchive.Create(full, archivePath, _key, CancellationToken.None);
            if (archive.ArchivedFiles.Count == 0 && archive.SkippedFiles.Count > 0)
            {
                DeleteQuietly(archivePath);
                entry.Status = SweepEntryStatus.Skipped;
                entry.Detail = "Every file is in use";
                return RemovalResult.Failed("Every file is in use");
            }

            sha = SweepArchive.Sha256Hex(archivePath);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(archivePath);
            throw;
        }
        catch (Exception e)
        {
            // Whatever went wrong, nothing has been removed yet: drop the partial archive and fail this entry.
            DeleteQuietly(archivePath);
            return Fail(index, e.Message);
        }

        // Persist the restoration index before touching any original. If this cannot be saved the
        // archive is useless to the user, so give it up and delete nothing.
        entry.Archive = archiveName;
        entry.Sha256 = sha;
        entry.FilesArchived = archive.ArchivedFiles.Count;
        entry.SkippedFiles = archive.SkippedFiles;
        entry.Status = SweepEntryStatus.Quarantined;
        entry.Detail = string.Empty;
        try
        {
            Manifest.Save(QuarantineVault.ManifestFile(_folder));
        }
        catch (Exception e)
        {
            // Any failure, not only IO: an index that is not on disk makes the archive unrestorable.
            DeleteQuietly(archivePath);
            entry.Archive = null;
            entry.Sha256 = null;
            entry.FilesArchived = 0;
            entry.SkippedFiles = new List<string>();
            return Fail(index, $"The sweep record could not be saved: {e.Message}");
        }

        BeforeDelete?.Invoke();

        long freed = 0;
        foreach (var file in archive.ArchivedFiles)
        {
            try
            {
                // A file that changed after it was archived is not fully in the archive: keep it.
                var (archivedLength, archivedWrite) = archive.Stamps[file];
                var info = new FileInfo(file);
                if (info.Length != archivedLength || info.LastWriteTimeUtc != archivedWrite)
                {
                    entry.SkippedFiles.Add(file);
                    continue;
                }

                if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, info.Attributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(file);
                freed += archivedLength;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                entry.SkippedFiles.Add(file);
            }
        }

        if (entry.IsDirectory)
        {
            RemoveEmptyDirectories(full);
        }

        // Quarantine() saves again after this, recording any files that were left behind.
        return RemovalResult.Ok(freed);
    }

    private static void RemoveEmptyDirectories(string root)
    {
        var directories = new List<string> { root };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            try
            {
                foreach (var child in Directory.EnumerateDirectories(pending.Pop()))
                {
                    if (!PathGuard.IsReparsePoint(child))
                    {
                        directories.Add(child);
                        pending.Push(child);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Unreadable folder: leave it and everything below it.
            }
        }

        foreach (var directory in directories.OrderByDescending(d => d.Length))
        {
            try
            {
                Directory.Delete(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not empty (a skipped file or something new is in it): keep it.
            }
        }
    }

    private int FindEntry(string path)
    {
        string full;
        try
        {
            full = PathGuard.Normalize(path);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return -1;
        }

        return Manifest.Entries.FindIndex(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
    }

    private RemovalResult Fail(int index, string reason)
    {
        if (index >= 0)
        {
            Manifest.Entries[index].Status = SweepEntryStatus.Failed;
            Manifest.Entries[index].Detail = reason;
        }

        return RemovalResult.Failed(reason);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing else to do with a leftover partial archive.
        }
    }
}
