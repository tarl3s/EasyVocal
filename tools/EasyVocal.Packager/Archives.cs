using System.Formats.Tar;
using System.IO.Compression;

namespace EasyVocal.Packager;

/// <summary>One file of a release archive: its path inside (forward slashes), what it holds and its Unix mode.</summary>
public sealed record ArchiveFile(string Path, int Mode, string? Source = null, byte[]? Bytes = null)
{
    public static ArchiveFile FromFile(string path, string source, int mode) => new(path, mode, Source: source);
    public static ArchiveFile FromBytes(string path, byte[] bytes, int mode) => new(path, mode, Bytes: bytes);

    public Stream Open() => Bytes != null ? new MemoryStream(Bytes, writable: false) : File.OpenRead(Source!);
}

/// <summary>
/// Zips and tar.gz files written from any OS with the Unix permission bits macOS and Linux need (the program must
/// arrive executable): zip entries carry the mode in the high half of their external attributes and say they were made
/// on Unix; tar entries carry it in their header.
/// </summary>
public static class Archives
{
    /// <summary>rwxr-xr-x: programs, libraries, scripts and folders.</summary>
    public const int Executable = 0x1ED; // 0755
    /// <summary>rw-r--r--: everything else.</summary>
    public const int Regular = 0x1A4; // 0644

    private const int RegularFileType = 0x8000, DirectoryType = 0x4000;
    private const byte MadeOnUnix = 3;

    /// <param name="unix">Write folders and Unix modes (macOS); false = a plain zip (Windows).</param>
    public static void WriteZip(string zipPath, IReadOnlyList<ArchiveFile> files, bool unix)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            if (unix)
                foreach (var folder in Folders(files))
                {
                    var entry = zip.CreateEntry(folder + "/", CompressionLevel.NoCompression);
                    entry.ExternalAttributes = UnixAttributes(DirectoryType | Executable) | 0x10; // + MS-DOS "directory"
                }
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Path, CompressionLevel.Optimal);
                if (unix) entry.ExternalAttributes = UnixAttributes(RegularFileType | file.Mode);
                using var to = entry.Open();
                using var from = file.Open();
                from.CopyTo(to);
            }
        }
        if (unix) MarkMadeOnUnix(zipPath);
    }

    private static int UnixAttributes(int mode) => unchecked((int)((uint)mode << 16));

    /// <summary>
    /// Sets "version made by" to Unix in every central directory header: unzip and macOS' Archive Utility only apply
    /// the mode in the external attributes of entries made on Unix (.NET writes the current OS there).
    /// </summary>
    public static void MarkMadeOnUnix(string zipPath)
    {
        using var f = new FileStream(zipPath, FileMode.Open, FileAccess.ReadWrite);
        long eocd = FindEndOfCentralDirectory(f);
        var tail = new byte[22];
        f.Position = eocd;
        f.ReadExactly(tail);
        int entries = BitConverter.ToUInt16(tail, 10);
        long size = BitConverter.ToUInt32(tail, 12), start = BitConverter.ToUInt32(tail, 16);
        var cd = new byte[size];
        f.Position = start;
        f.ReadExactly(cd);
        int at = 0;
        for (int i = 0; i < entries; i++)
        {
            if (BitConverter.ToUInt32(cd, at) != 0x02014B50) throw new InvalidDataException("Broken central directory in " + zipPath);
            cd[at + 5] = MadeOnUnix;
            at += 46 + BitConverter.ToUInt16(cd, at + 28) + BitConverter.ToUInt16(cd, at + 30) + BitConverter.ToUInt16(cd, at + 32);
        }
        f.Position = start;
        f.Write(cd);
    }

    private static long FindEndOfCentralDirectory(FileStream f)
    {
        int window = (int)Math.Min(f.Length, 22 + 65535);
        var buffer = new byte[window];
        f.Position = f.Length - window;
        f.ReadExactly(buffer);
        for (int i = window - 22; i >= 0; i--)
            if (buffer[i] == 0x50 && buffer[i + 1] == 0x4B && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
                return f.Length - window + i;
        throw new InvalidDataException("No end of central directory in " + f.Name);
    }

    /// <summary>A gzip'd ustar archive: folders 0755, each file with its own mode, owned by root.</summary>
    public static void WriteTarGz(string path, IReadOnlyList<ArchiveFile> files)
    {
        if (File.Exists(path)) File.Delete(path);
        var now = DateTimeOffset.UtcNow;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var gzip = new GZipStream(stream, CompressionLevel.Optimal);
        using var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: false);
        foreach (var folder in Folders(files))
            tar.WriteEntry(new UstarTarEntry(TarEntryType.Directory, folder + "/")
            {
                Mode = (UnixFileMode)Executable, ModificationTime = now, UserName = "root", GroupName = "root",
            });
        foreach (var file in files)
        {
            using var data = file.Open();
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, file.Path)
            {
                Mode = (UnixFileMode)file.Mode, ModificationTime = now, UserName = "root", GroupName = "root", DataStream = data,
            });
        }
    }

    /// <summary>Every folder the files are in (parents first).</summary>
    public static IReadOnlyList<string> Folders(IEnumerable<ArchiveFile> files)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
            for (int slash = file.Path.IndexOf('/'); slash > 0; slash = file.Path.IndexOf('/', slash + 1))
                set.Add(file.Path[..slash]);
        return set.ToList();
    }

    /// <summary>The files of a publish folder, as paths under <paramref name="prefix"/> with forward slashes. Debug symbols
    /// (.pdb, which the referenced projects still copy) are left out.</summary>
    public static IEnumerable<(string Path, string Source)> FilesIn(string folder, string prefix)
    {
        var root = Path.GetFullPath(folder);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            yield return (prefix + Path.GetRelativePath(root, file).Replace('\\', '/'), file);
    }
}

/// <summary>
/// <c>easyvocal-files.txt</c>: every file of the package, one per line, relative to its install root (the folder with
/// EasyVocal.exe, the .app, the EasyVocal folder), itself included. It marks a folder as an EasyVocal install, so the
/// updater can refuse to replace anything else (a Downloads folder).
/// </summary>
public static class Manifest
{
    public const string FileName = "easyvocal-files.txt";

    /// <param name="root">The install root inside the archive ("" for the top, else e.g. "EasyVocal.app/").</param>
    /// <param name="at">Where the manifest goes, relative to <paramref name="root"/>.</param>
    public static List<ArchiveFile> Add(IReadOnlyList<ArchiveFile> files, string root, string at)
    {
        var lines = files.Select(f => f.Path).Append(root + at)
            .Select(p => p.StartsWith(root, StringComparison.Ordinal) ? p[root.Length..] : throw new ArgumentException(p + " is outside " + root))
            .Order(StringComparer.Ordinal);
        var text = string.Join("\n", lines) + "\n";
        return files.Append(ArchiveFile.FromBytes(root + at, System.Text.Encoding.UTF8.GetBytes(text), Archives.Regular)).ToList();
    }
}
