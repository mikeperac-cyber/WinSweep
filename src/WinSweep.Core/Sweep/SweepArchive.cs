using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using WinSweep.Core.Safety;

namespace WinSweep.Core.Sweep;

internal sealed record ArchiveResult(List<string> ArchivedFiles, List<string> SkippedFiles, long Bytes);

/// <summary>Reads and writes the .wsq format: [16-byte IV][AES-256-CBC(GZip(tar))].</summary>
internal static class SweepArchive
{
    private const int IvLength = 16;

    /// <summary>Writes the archive of a file or folder. Never modifies the source.</summary>
    internal static ArchiveResult Create(string sourcePath, string archivePath, byte[] key, CancellationToken ct)
    {
        var archived = new List<string>();
        var skipped = new List<string>();
        long bytes = 0;

        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(aes.IV);
        using var crypto = new CryptoStream(file, aes.CreateEncryptor(), CryptoStreamMode.Write);
        using var gzip = new GZipStream(crypto, CompressionLevel.Fastest);
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            if (File.Exists(sourcePath))
            {
                AddFile(tar, sourcePath, Path.GetFileName(sourcePath), archived, skipped, ref bytes, ct);
            }
            else
            {
                var pending = new Stack<(string Directory, string Prefix)>();
                pending.Push((sourcePath, string.Empty));
                while (pending.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var (directory, prefix) = pending.Pop();
                    List<string> children;
                    try
                    {
                        children = Directory.EnumerateFileSystemEntries(directory).ToList();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        skipped.Add(directory);
                        continue;
                    }

                    foreach (var child in children)
                    {
                        var name = prefix + Path.GetFileName(child);
                        FileAttributes attributes;
                        try
                        {
                            attributes = File.GetAttributes(child);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            skipped.Add(child);
                            continue;
                        }

                        if (attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            skipped.Add(child);
                        }
                        else if (attributes.HasFlag(FileAttributes.Directory))
                        {
                            // An explicit entry keeps empty folders.
                            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name + "/"));
                            pending.Push((child, name + "/"));
                        }
                        else
                        {
                            AddFile(tar, child, name, archived, skipped, ref bytes, ct);
                        }
                    }
                }
            }
        }

        return new ArchiveResult(archived, skipped, bytes);
    }

    private static void AddFile(TarWriter tar, string path, string name, List<string> archived,
        List<string> skipped, ref long bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (PathGuard.IsReparsePoint(path))
        {
            skipped.Add(path);
            return;
        }

        // Open before writing anything, so a locked file leaves no half entry in the tar.
        FileStream fs;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            skipped.Add(path);
            return;
        }

        using (fs)
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = fs,
                ModificationTime = File.GetLastWriteTimeUtc(path)
            });
            archived.Add(path);
            bytes += fs.Length;
        }
    }

    /// <summary>
    /// Extracts into <paramref name="destinationPath"/> (a directory target) or as that file (a file target).
    /// Files that already exist are left alone and counted.
    /// </summary>
    internal static (int Restored, int SkippedExisting) Extract(string archivePath, string destinationPath,
        bool isDirectory, byte[] key, CancellationToken ct)
    {
        var root = Path.GetFullPath(isDirectory ? destinationPath : Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        Directory.CreateDirectory(root);

        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var iv = new byte[IvLength];
        file.ReadExactly(iv);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        using var crypto = new CryptoStream(file, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var gzip = new GZipStream(crypto, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        var restored = 0;
        var skippedExisting = 0;
        while (tar.GetNextEntry(copyData: false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.Name;
            if (Path.IsPathRooted(name) || name.Contains(':') ||
                name.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                name.Split('/', '\\').Contains(".."))
            {
                throw new InvalidDataException($"Unsafe entry name in archive: {name}");
            }

            string destination;
            if (isDirectory)
            {
                destination = Path.GetFullPath(Path.Combine(root, name));
                if (!PathGuard.IsSameOrUnder(destination, root))
                {
                    throw new InvalidDataException($"Entry escapes the destination: {name}");
                }
            }
            else
            {
                if (entry.EntryType != TarEntryType.RegularFile)
                {
                    continue;
                }

                if (!string.Equals(name, Path.GetFileName(destinationPath), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Unexpected entry in a single-file archive: {name}");
                }

                destination = Path.GetFullPath(destinationPath);
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(destination);
            }
            else if (entry.EntryType == TarEntryType.RegularFile)
            {
                if (File.Exists(destination))
                {
                    skippedExisting++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);
                restored++;
            }
        }

        return (restored, skippedExisting);
    }

    /// <summary>Lowercase hex SHA-256 of a file, streamed.</summary>
    internal static string Sha256Hex(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
