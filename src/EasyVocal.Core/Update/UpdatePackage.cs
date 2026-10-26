using System.Formats.Tar;
using System.IO.Compression;
using EasyVocal.Core.Storage;

namespace EasyVocal.Core.Update;

/// <summary>
/// Unpacks downloaded releases with the same safety checks for every platform: no entry outside its folder (zip-slip),
/// no links, a size limit; Unix permission bits (the executable bit) are kept on macOS and Linux.
/// </summary>
public static class UpdatePackage
{
    /// <summary>A release that unpacks to more than this is not a release (a "zip bomb").</summary>
    public const long MaxUnpackedBytes = 600L * 1024 * 1024;

    /// <summary>
    /// Checks the zip at <paramref name="zipPath"/> (every entry stays inside its folder, the size limit) and writes its
    /// <paramref name="fileName"/> (at the top or in its one top folder) to <paramref name="folder"/>. Returns its path.
    /// A cancellation while it is written stops it and removes what was written. The platform checks the file itself.
    /// </summary>
    public static string ExtractFile(string zipPath, string folder, string fileName, CancellationToken ct = default)
    {
        var root = Root(folder);
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException ex) { throw new UpdateException(UpdateProblem.NotAZip, "The download is not a zip file.", ex); }
        using (archive)
        {
            ZipArchiveEntry? found = null;
            int foundDepth = int.MaxValue;
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                SafeTarget(root, entry.FullName);
                total += Math.Max(0, entry.Length);
                if (total > MaxUnpackedBytes) throw TooBig();
                if (!string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase)) continue;
                int depth = entry.FullName.Replace('\\', '/').Trim('/').Split('/').Length;
                if (depth <= 2 && depth < foundDepth) { found = entry; foundDepth = depth; }
            }
            if (found == null) throw new UpdateException(UpdateProblem.MissingExe, "The download doesn't contain " + fileName + ".");

            var dest = Path.Combine(root, fileName);
            ct.ThrowIfCancellationRequested();
            long budget = MaxUnpackedBytes;
            Writing(fileName, () =>
            {
                Directory.CreateDirectory(root);
                using var from = found.Open();
                CopyOut(from, dest, ref budget, ct);
            });
            SetMode(dest, UnixMode(found));
            return dest;
        }
    }

    /// <summary>
    /// One write of the unpacking. A file system error (a file where the archive wants a folder, as with the entries "a"
    /// and "a/b"; a name the system refuses) means a broken download, not a crash.
    /// </summary>
    private static void Writing(string entryName, Action write)
    {
        try { write(); }
        catch (Exception ex) when (ex is (IOException and not EndOfStreamException) or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateProblem.UnsafeZip, "The download's " + entryName + " can't be written: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Unpacks a whole zip or tar.gz (told apart by their first bytes) into <paramref name="folder"/>, with the checks
    /// above; symbolic and hard links are refused. A cancellation stops it (what was written stays for the caller to remove).
    /// </summary>
    public static void ExtractAll(string archivePath, string folder, CancellationToken ct = default, long maxBytes = MaxUnpackedBytes)
    {
        var root = Root(folder);
        Directory.CreateDirectory(root);
        if (IsGzip(archivePath)) ExtractTarGz(archivePath, root, ct, maxBytes);
        else ExtractZip(archivePath, root, ct, maxBytes);
    }

    private static void ExtractZip(string zipPath, string root, CancellationToken ct, long maxBytes)
    {
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException ex) { throw new UpdateException(UpdateProblem.NotAZip, "The download is not a zip or tar.gz file.", ex); }
        using (archive)
        {
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                SafeTarget(root, entry.FullName);
                total += Math.Max(0, entry.Length);
                if (total > maxBytes) throw TooBig();
            }
            long budget = maxBytes; // the sizes above are what the zip claims; this counts what really comes out
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = SafeTarget(root, entry.FullName);
                var mode = UnixMode(entry);
                if (mode.HasValue && (mode.Value & UnixTypeMask) == UnixSymlink)
                    throw new UpdateException(UpdateProblem.UnsafeZip, "The download contains a link: " + entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Writing(entry.FullName, () => Directory.CreateDirectory(target));
                    continue;
                }
                Writing(entry.FullName, () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var from = entry.Open();
                    CopyOut(from, target, ref budget, ct);
                });
                SetMode(target, mode);
            }
        }
    }

    private static void ExtractTarGz(string path, string root, CancellationToken ct, long maxBytes)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            long total = 0, budget = maxBytes;
            while (tar.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                var target = SafeTarget(root, entry.Name);
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Writing(entry.Name, () => Directory.CreateDirectory(target));
                        continue;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        total += Math.Max(0, entry.Length);
                        if (total > maxBytes) throw TooBig();
                        Writing(entry.Name, () =>
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            if (entry.DataStream is { } data) CopyOut(data, target, ref budget, ct);
                            else File.WriteAllBytes(target, Array.Empty<byte>());
                        });
                        SetMode(target, (int)entry.Mode);
                        continue;
                    case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                        throw new UpdateException(UpdateProblem.UnsafeZip, "The download contains a link: " + entry.Name);
                    default:
                        continue; // pax/GNU headers and the like carry no file
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new UpdateException(UpdateProblem.NotAZip, "The download is not a valid tar.gz file.", ex);
        }
    }

    private static string Root(string folder) =>
        Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>Where an entry lands; an entry outside <paramref name="root"/> (or with a broken name) is an error.</summary>
    private static string SafeTarget(string root, string entryName)
    {
        string target;
        try { target = Path.GetFullPath(Path.Combine(root, entryName.Replace('\\', '/'))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new UpdateException(UpdateProblem.UnsafeZip, "The download has a file with a broken name.", ex);
        }
        if (!PathCompare.IsUnder(target, root) && !PathCompare.Same(target + Path.DirectorySeparatorChar, root))
            throw new UpdateException(UpdateProblem.UnsafeZip, "The download contains a file outside its folder: " + entryName);
        return target;
    }

    private static UpdateException TooBig() => new(UpdateProblem.TooBig, "The download unpacks to more than expected.");

    private static bool IsGzip(string path)
    {
        using var f = File.OpenRead(path);
        return f.ReadByte() == 0x1F && f.ReadByte() == 0x8B;
    }

    private const int UnixTypeMask = 0xF000, UnixSymlink = 0xA000;

    /// <summary>The Unix mode a zip made on macOS/Linux stores in the high half of its external attributes, or null.</summary>
    private static int? UnixMode(ZipArchiveEntry entry)
    {
        int mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        return mode == 0 ? null : mode;
    }

    /// <summary>The permission bits kept from an archive: read/write/execute only (never setuid, setgid or sticky).</summary>
    public const int KeptModeBits = 0x1FF;

    /// <summary>Keeps the permission bits (the executable one above all) where the system has them.</summary>
    private static void SetMode(string path, int? mode)
    {
        if (mode is not { } m || OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, (UnixFileMode)(m & KeptModeBits)); }
        catch (Exception) { /* a file system without permissions: nothing to keep */ }
    }

    /// <summary>
    /// Writes the stream to <paramref name="dest"/> in chunks, so a cancellation stops it quickly (the exe is ~80 MB).
    /// <paramref name="budget"/> is what may still be written for the whole archive: an entry that unpacks to more than
    /// its header said (a lying zip) is stopped there.
    /// </summary>
    private static void CopyOut(Stream from, string dest, ref long budget, CancellationToken ct)
    {
        bool tooBig = false;
        using (var to = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int n;
            while (!ct.IsCancellationRequested && (n = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                budget -= n;
                if (budget < 0) { tooBig = true; break; }
                to.Write(buffer, 0, n);
            }
        }
        if (tooBig)
        {
            Cleanup.TryDelete(dest);
            throw TooBig();
        }
        if (!ct.IsCancellationRequested) return;
        Cleanup.TryDelete(dest);
        ct.ThrowIfCancellationRequested();
    }
}
