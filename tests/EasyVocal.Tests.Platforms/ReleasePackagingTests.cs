using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using EasyVocal.Packager;
using EasyVocal.Platform.Unix;
using Xunit.Abstractions;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests.Platforms;

/// <summary>
/// tools/EasyVocal.Packager, the code tools/Publish-Release.ps1 makes the downloads with, run on pretend publish folders:
/// the .app's Info.plist and .icns, the Unix modes in the zip and the tar.gz, the Linux extras, and that each platform's
/// updater can unpack what it makes.
/// </summary>
public sealed class ReleasePackagingTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string temp = Path.Combine(Path.GetTempPath(), "EV_PackTest_" + Guid.NewGuid().ToString("N")[..8]);

    public ReleasePackagingTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(temp);
    }

    public void Dispose()
    {
        TestFolders.Delete(temp);
    }

    private const int Exec = 0x1ED, Plain = 0x1A4;

    /// <summary>A thin 64-bit Mach-O header with no load commands (arm64 or x86_64).</summary>
    private static byte[] MachO(bool arm64)
    {
        var b = new byte[4096];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFEEDFACF);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), arm64 ? 0x0100000C : 0x01000007);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), 2); // MH_EXECUTE
        return b;
    }

    private static byte[] Elf() => new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>Enough of a PNG for its IHDR width (the packager never decodes the picture).</summary>
    private static byte[] Png(int size)
    {
        var b = new byte[64];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), size);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), size);
        b[63] = (byte)(size & 0xFF);
        return b;
    }

    private static readonly IReadOnlyList<byte[]> Icons = new[] { 16, 32, 64, 128, 256, 512, 1024 }.Select(Png).ToList();

    private string PublishFolder(string name, params (string File, byte[] Bytes)[] files)
    {
        var folder = Path.Combine(temp, name);
        Directory.CreateDirectory(folder);
        foreach (var (file, bytes) in files) File.WriteAllBytes(Path.Combine(folder, file), bytes);
        return folder;
    }

    private static int Mode(ZipArchiveEntry e) => (e.ExternalAttributes >> 16) & 0xFFFF;

    [Fact]
    public void AssetNames_AreTheOnesTheUpdatersAndTheReadmesUse()
    {
        Assert.Equal(Release.MacArm64, UnixUpdaterPlatform.MacOS.AssetNames(Architecture.Arm64)[0]);
        Assert.Equal(Release.MacX64, UnixUpdaterPlatform.MacOS.AssetNames(Architecture.X64)[0]);
        Assert.Equal(Release.LinuxX64, UnixUpdaterPlatform.Linux.AssetNames(Architecture.X64)[0]);
        Assert.Equal(new[] { Release.WindowsX64, Release.MacArm64, Release.MacX64, Release.LinuxX64 }, Release.Rids.Select(Release.AssetName));

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var readme in new[] { "README.md", "README.pt-BR.md" })
        {
            var text = File.ReadAllText(Path.Combine(root!.FullName, readme));
            foreach (var rid in Release.Rids)
                Assert.Contains("releases/latest/download/" + Release.AssetName(rid), text);
        }
    }

    [Fact]
    public void Mac_AppBundle_PlistIcnsModes_AndTheUpdaterUnpacksIt()
    {
        var publish = PublishFolder("osx-arm64", ("EasyVocal", MachO(arm64: true)), ("libportaudio.dylib", new byte[] { 1, 2, 3 }),
            ("libSkiaSharp.dylib", new byte[] { 4 }), ("EasyVocal.deps.json", "{}"u8.ToArray()));
        var packed = Release.Pack("osx-arm64", publish, Path.Combine(temp, "out"), "1.2.3", Icons);
        Assert.Equal(Release.MacArm64, Path.GetFileName(packed.Archive));
        Assert.Contains("arm64", packed.Note);

        using (var zip = ZipFile.OpenRead(packed.Archive))
        {
            var entries = zip.Entries.ToDictionary(e => e.FullName);
            const string contents = "EasyVocal.app/Contents/";
            Assert.Equal(0x41ED, Mode(entries["EasyVocal.app/"]));
            Assert.Equal(0x41ED, Mode(entries[contents + "MacOS/"]));
            Assert.Equal(0x8000 | Exec, Mode(entries[contents + "MacOS/EasyVocal"]));
            Assert.Equal(0x8000 | Exec, Mode(entries[contents + "MacOS/libportaudio.dylib"]));
            Assert.Equal(0x8000 | Plain, Mode(entries[contents + "MacOS/EasyVocal.deps.json"]));
            Assert.Equal(0x8000 | Plain, Mode(entries[contents + "Info.plist"]));
            var manifest = new StreamReader(entries[contents + "Resources/easyvocal-files.txt"].Open()).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("Contents/MacOS/EasyVocal", manifest);
            Assert.Contains("Contents/Info.plist", manifest);
            Assert.Contains("Contents/Resources/easyvocal-files.txt", manifest);
            Assert.Equal(zip.Entries.Count(e => !e.FullName.EndsWith('/')), manifest.Length);
            Assert.All(zip.Entries, e => Assert.NotEqual(0xA000, Mode(e) & 0xF000)); // no symbolic links
            Assert.Equal("APPL????", new StreamReader(entries[contents + "PkgInfo"].Open()).ReadToEnd());

            var plist = PlistValues(new StreamReader(entries[contents + "Info.plist"].Open()).ReadToEnd());
            Assert.Equal("com.tarl3s.easyvocal", plist["CFBundleIdentifier"]);
            Assert.Equal("EasyVocal", plist["CFBundleName"]);
            Assert.Equal("EasyVocal", plist["CFBundleExecutable"]);
            Assert.Equal("1.2.3", plist["CFBundleShortVersionString"]);
            Assert.Equal("1.2.3", plist["CFBundleVersion"]);
            Assert.Equal("11.0", plist["LSMinimumSystemVersion"]);
            Assert.Equal("EasyVocal needs the microphone to change your voice.", plist["NSMicrophoneUsageDescription"]);
            Assert.Equal("false", plist["LSUIElement"]);
            Assert.Equal("true", plist["NSHighResolutionCapable"]);
            Assert.Equal("APPL", plist["CFBundlePackageType"]);
            Assert.Equal("EasyVocal.icns", plist["CFBundleIconFile"]);

            using var icnsStream = new MemoryStream();
            entries[contents + "Resources/EasyVocal.icns"].Open().CopyTo(icnsStream);
            var types = IcnsChunks(icnsStream.ToArray());
            foreach (var t in new[] { "ic07", "ic08", "ic09", "ic10", "icp4", "icp5", "ic13", "ic14" }) Assert.Contains(t, types);
        }
        Assert.All(MadeBy(packed.Archive), b => Assert.Equal(3, b)); // made on Unix, so unzip and Finder apply the modes

        var unpacked = UnixUpdaterPlatform.MacOS.Unpack(packed.Archive, Path.Combine(temp, "unpacked"), CancellationToken.None);
        Assert.Equal("EasyVocal.app", Path.GetFileName(unpacked));
        var program = Path.Combine(unpacked, "Contents", "MacOS", "EasyVocal");
        Assert.True(File.Exists(program));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(program).HasFlag(UnixFileMode.UserExecute), "the program lost its executable bit");
    }

    [Fact]
    public void Mac_RefusesTheWrongChipOrAMissingPortAudio()
    {
        var arm = PublishFolder("arm", ("EasyVocal", MachO(arm64: true)), ("libportaudio.dylib", new byte[] { 1 }));
        Assert.Throws<InvalidDataException>(() => Release.Pack("osx-x64", arm, Path.Combine(temp, "out"), "1.0.0", Icons));
        var intel = PublishFolder("intel", ("EasyVocal", MachO(arm64: false)), ("libportaudio.dylib", new byte[] { 1 }));
        Assert.Contains("x64", Release.Pack("osx-x64", intel, Path.Combine(temp, "out"), "1.0.0", Icons).Note);
        var noPortAudio = PublishFolder("nopa", ("EasyVocal", MachO(arm64: true)));
        Assert.Throws<FileNotFoundException>(() => Release.Pack("osx-arm64", noPortAudio, Path.Combine(temp, "out"), "1.0.0", Icons));
    }

    [Fact]
    public void Linux_TarGz_ModesDesktopInstallScript_AndTheUpdaterUnpacksIt()
    {
        var publish = PublishFolder("linux-x64", ("EasyVocal", Elf()), ("libportaudio.so", new byte[] { 1 }),
            ("libSkiaSharp.so", new byte[] { 2 }), ("EasyVocal.deps.json", "{}"u8.ToArray()), ("EasyVocal.Core.pdb", new byte[] { 9 }));
        var packed = Release.Pack("linux-x64", publish, Path.Combine(temp, "out"), "1.0.0", Icons);
        Assert.Equal(Release.LinuxX64, Path.GetFileName(packed.Archive));

        var modes = new Dictionary<string, (TarEntryType Type, int Mode, byte[] Data)>();
        using (var file = File.OpenRead(packed.Archive))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
            while (tar.GetNextEntry() is { } entry)
            {
                using var data = new MemoryStream();
                entry.DataStream?.CopyTo(data);
                modes[entry.Name] = (entry.EntryType, (int)entry.Mode, data.ToArray());
            }
        Assert.All(modes.Values, m => Assert.True(m.Type is TarEntryType.Directory or TarEntryType.RegularFile, "only folders and files (no links)"));
        var listed = Encoding.UTF8.GetString(modes["EasyVocal/easyvocal-files.txt"].Data).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(modes.Keys.Where(k => !k.EndsWith('/')).Select(k => k["EasyVocal/".Length..]).Order(StringComparer.Ordinal), listed);
        Assert.Equal((TarEntryType.Directory, Exec), (modes["EasyVocal/"].Type, modes["EasyVocal/"].Mode));
        Assert.DoesNotContain("EasyVocal/EasyVocal.Core.pdb", modes.Keys); // debug symbols stay out
        Assert.Equal(Exec, modes["EasyVocal/EasyVocal"].Mode);
        Assert.Equal(Exec, modes["EasyVocal/libportaudio.so"].Mode);
        Assert.Equal(Exec, modes["EasyVocal/install.sh"].Mode);
        Assert.Equal(Plain, modes["EasyVocal/EasyVocal.deps.json"].Mode);
        Assert.Equal(Plain, modes["EasyVocal/easyvocal.png"].Mode);
        Assert.Equal(256, MacApp.PngWidth(modes["EasyVocal/easyvocal.png"].Data));
        var desktop = Encoding.UTF8.GetString(modes["EasyVocal/easyvocal.desktop"].Data);
        Assert.Contains("[Desktop Entry]\n", desktop);
        Assert.Contains("\nIcon=easyvocal\n", desktop);
        Assert.Contains("\nExec=", desktop);
        var script = Encoding.UTF8.GetString(modes["EasyVocal/install.sh"].Data);
        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.DoesNotContain("\r", script);

        var unpacked = UnixUpdaterPlatform.Linux.Unpack(packed.Archive, Path.Combine(temp, "unpacked"), CancellationToken.None);
        Assert.Equal("EasyVocal", Path.GetFileName(unpacked));
        Assert.True(File.Exists(Path.Combine(unpacked, "install.sh")));
    }

    [Fact]
    public void Linux_InstallScript_CopiesTheFolderAndAddsTheMenuEntry()
    {
        if (OperatingSystem.IsWindows())
        {
            output.WriteLine("SKIPPED on Windows: install.sh needs a Unix sh");
            return;
        }
        var publish = PublishFolder("linux-x64", ("EasyVocal", Elf()), ("libportaudio.so", new byte[] { 1 }));
        var packed = Release.Pack("linux-x64", publish, Path.Combine(temp, "out"), "1.0.0", Icons);
        var extracted = Path.Combine(temp, "downloads");
        UnixUpdaterPlatform.Linux.Unpack(packed.Archive, extracted, CancellationToken.None);
        var data = Path.Combine(temp, "data home $x");
        var psi = new ProcessStartInfo("/bin/sh", new[] { Path.Combine(extracted, "EasyVocal", "install.sh") })
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment["XDG_DATA_HOME"] = data;
        using var sh = Process.Start(psi)!;
        string stdout = sh.StandardOutput.ReadToEnd(), stderr = sh.StandardError.ReadToEnd();
        Assert.True(sh.WaitForExit(30_000));
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, sh.ExitCode);
        var program = Path.Combine(data, "EasyVocal", "EasyVocal");
        Assert.True(File.Exists(program));
        Assert.True(File.GetUnixFileMode(program).HasFlag(UnixFileMode.UserExecute));
        Assert.True(File.Exists(Path.Combine(data, "icons", "hicolor", "256x256", "apps", "easyvocal.png")));
        var entry = File.ReadAllText(Path.Combine(data, "applications", "easyvocal.desktop"));
        // The .desktop format: the program quoted, $ escaped by a backslash that is itself doubled.
        Assert.Contains("Exec=\"" + program.Replace("$", "\\\\$") + "\"\n", entry);
        Assert.Single(entry.Split('\n'), l => l.StartsWith("Exec=", StringComparison.Ordinal));
    }

    [Fact]
    public void Windows_ZipHoldsTheExeAtTheTop()
    {
        var publish = PublishFolder("win-x64", ("EasyVocal.exe", "MZ-not-really"u8.ToArray()));
        var packed = Release.Pack("win-x64", publish, Path.Combine(temp, "out"), "1.0.0", Icons);
        Assert.Equal(Release.WindowsX64, Path.GetFileName(packed.Archive));
        using var zip = ZipFile.OpenRead(packed.Archive);
        Assert.Equal(new[] { "EasyVocal.exe", "easyvocal-files.txt" }, zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal("EasyVocal.exe\neasyvocal-files.txt\n", new StreamReader(zip.GetEntry("easyvocal-files.txt")!.Open()).ReadToEnd());
        var notExe = PublishFolder("bad", ("EasyVocal.exe", "PK"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => Release.Pack("win-x64", notExe, Path.Combine(temp, "out2"), "1.0.0", Icons));
    }

    [Fact]
    public void Icns_HeaderLengthsAndSlots()
    {
        var icns = MacApp.Icns(new[] { Png(128), Png(1024), Png(48) });
        Assert.Equal("icns", Encoding.ASCII.GetString(icns, 0, 4));
        Assert.Equal(icns.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(4)));
        Assert.Equal(new[] { "ic07", "ic10" }, IcnsChunks(icns)); // 48 px has no slot
        Assert.Throws<InvalidDataException>(() => MacApp.Icns(new[] { Png(48) }));
    }

    /// <summary>The chunk types of an .icns, checking every chunk's length adds up to the file's.</summary>
    private static List<string> IcnsChunks(byte[] icns)
    {
        var types = new List<string>();
        int at = 8;
        while (at < icns.Length)
        {
            types.Add(Encoding.ASCII.GetString(icns, at, 4));
            at += (int)BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(at + 4));
        }
        Assert.Equal(icns.Length, at);
        return types;
    }

    /// <summary>The key → value pairs of a flat plist dict (true/false as "true"/"false").</summary>
    private static Dictionary<string, string> PlistValues(string xml)
    {
        var dict = XDocument.Parse(xml).Root!.Element("dict")!.Elements().ToList();
        var values = new Dictionary<string, string>();
        for (int i = 0; i + 1 < dict.Count; i += 2)
        {
            Assert.Equal("key", dict[i].Name.LocalName);
            var v = dict[i + 1];
            values[dict[i].Value] = v.Name.LocalName is "true" or "false" ? v.Name.LocalName : v.Value;
        }
        return values;
    }

    /// <summary>The high byte of "version made by" of every central directory header (3 = Unix).</summary>
    private static List<int> MadeBy(string zipPath)
    {
        var bytes = File.ReadAllBytes(zipPath);
        int eocd = bytes.Length - 22;
        while (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd)) != 0x06054B50) eocd--;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(eocd + 10));
        int at = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd + 16));
        var made = new List<int>();
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(0x02014B50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)));
            made.Add(bytes[at + 5]);
            at += 46 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 28)) + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 30))
                + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 32));
        }
        Assert.NotEmpty(made);
        return made;
    }
}
