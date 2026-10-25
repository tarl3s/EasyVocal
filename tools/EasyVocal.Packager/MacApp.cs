using System.Buffers.Binary;
using System.Security;
using System.Text;

namespace EasyVocal.Packager;

/// <summary>
/// EasyVocal.app: <c>Contents/Info.plist</c>, <c>Contents/PkgInfo</c>, <c>Contents/MacOS/</c> (the program and its
/// native libraries, PortAudio among them) and <c>Contents/Resources/EasyVocal.icns</c>. Unsigned: macOS' codesign
/// doesn't exist on Windows (the README tells people how to open it).
/// </summary>
public static class MacApp
{
    public const string BundleName = "EasyVocal.app";
    public const string Executable = "EasyVocal";
    public const string BundleIdentifier = "com.tarl3s.easyvocal";
    public const string MinimumSystemVersion = "11.0";
    public const string MicrophoneUsage = "EasyVocal needs the microphone to change your voice.";
    public const string IconFile = "EasyVocal.icns";

    public static string InfoPlist(string version)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        void Text(string key, string value) =>
            sb.Append("    <key>").Append(key).Append("</key>\n    <string>").Append(SecurityElement.Escape(value)).Append("</string>\n");
        void Flag(string key, bool value) =>
            sb.Append("    <key>").Append(key).Append("</key>\n    <").Append(value ? "true" : "false").Append("/>\n");
        Text("CFBundleDevelopmentRegion", "en");
        Text("CFBundleDisplayName", "EasyVocal");
        Text("CFBundleExecutable", Executable);
        Text("CFBundleIconFile", IconFile);
        Text("CFBundleIdentifier", BundleIdentifier);
        Text("CFBundleInfoDictionaryVersion", "6.0");
        Text("CFBundleName", "EasyVocal");
        Text("CFBundlePackageType", "APPL");
        Text("CFBundleShortVersionString", version);
        Text("CFBundleVersion", version);
        Text("LSApplicationCategoryType", "public.app-category.utilities");
        Text("LSMinimumSystemVersion", MinimumSystemVersion);
        Flag("LSUIElement", false);
        Text("NSMicrophoneUsageDescription", MicrophoneUsage);
        Flag("NSHighResolutionCapable", true);
        Text("NSPrincipalClass", "NSApplication");
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    /// <summary>The .app's files from a <c>dotnet publish -r osx-*</c> folder; the program and the libraries are 0755.</summary>
    public static IReadOnlyList<ArchiveFile> Bundle(string publishFolder, string version, byte[] icns)
    {
        var files = new List<ArchiveFile>
        {
            ArchiveFile.FromBytes(BundleName + "/Contents/Info.plist", Encoding.UTF8.GetBytes(InfoPlist(version)), Archives.Regular),
            ArchiveFile.FromBytes(BundleName + "/Contents/PkgInfo", Encoding.ASCII.GetBytes("APPL????"), Archives.Regular),
        };
        foreach (var (path, source) in Archives.FilesIn(publishFolder, BundleName + "/Contents/MacOS/"))
        {
            var name = Path.GetFileName(path);
            bool exec = name == Executable || name.EndsWith(".dylib", StringComparison.Ordinal);
            files.Add(ArchiveFile.FromFile(path, source, exec ? Archives.Executable : Archives.Regular));
        }
        files.Add(ArchiveFile.FromBytes(BundleName + "/Contents/Resources/" + IconFile, icns, Archives.Regular));
        return files;
    }

    /// <summary>
    /// An .icns made of PNGs ("icns" + total length, then one chunk per image: 4-letter type + length + PNG). Each PNG
    /// goes in every slot its width fits (e.g. 256 px = ic08 "256x256" and ic13 "128x128@2x").
    /// </summary>
    public static byte[] Icns(IEnumerable<byte[]> pngs)
    {
        var chunks = new List<(string Type, byte[] Png)>();
        foreach (var png in pngs)
            foreach (var type in IcnsTypes(PngWidth(png)))
                chunks.Add((type, png));
        if (chunks.Count == 0) throw new InvalidDataException("No PNG of an icns size (16, 32, 64, 128, 256, 512, 1024)");
        using var ms = new MemoryStream();
        var word = new byte[4];
        void Write(uint value) { BinaryPrimitives.WriteUInt32BigEndian(word, value); ms.Write(word); }
        ms.Write("icns"u8);
        Write((uint)(8 + chunks.Sum(c => 8 + c.Png.Length)));
        foreach (var (type, png) in chunks)
        {
            ms.Write(Encoding.ASCII.GetBytes(type));
            Write((uint)(8 + png.Length));
            ms.Write(png);
        }
        return ms.ToArray();
    }

    public static IReadOnlyList<string> IcnsTypes(int width) => width switch
    {
        16 => new[] { "icp4" },
        32 => new[] { "icp5", "ic11" },
        64 => new[] { "icp6", "ic12" },
        128 => new[] { "ic07" },
        256 => new[] { "ic08", "ic13" },
        512 => new[] { "ic09", "ic14" },
        1024 => new[] { "ic10" },
        _ => Array.Empty<string>(),
    };

    /// <summary>The width in a PNG's IHDR (bytes 16-19, big-endian).</summary>
    public static int PngWidth(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G')
            throw new InvalidDataException("Not a PNG");
        return (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
    }

    /// <summary>What a Mach-O file is: its architecture and whether it carries a code signature (LC_CODE_SIGNATURE).</summary>
    public sealed record MachO(string Architecture, bool Signed);

    /// <summary>A thin 64-bit or universal Mach-O, or null for anything else.</summary>
    public static MachO? ReadMachO(string path)
    {
        var bytes = new byte[64 * 1024];
        int n;
        using (var f = File.OpenRead(path)) n = f.Read(bytes, 0, bytes.Length);
        if (n < 32) return null;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (magic is 0xBEBAFECA) return new MachO("universal", Signed: false);
        if (magic != 0xFEEDFACF) return null;
        int cpu = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        int commands = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
        bool signed = false;
        for (int i = 0, at = 32; i < commands && at + 8 <= n; i++)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            if (cmd == 0x1D) signed = true; // LC_CODE_SIGNATURE
            if (size <= 0) break;
            at += size;
        }
        string arch = cpu switch { 0x01000007 => "x64", 0x0100000C => "arm64", _ => "cpu " + cpu.ToString("X") };
        return new MachO(arch, signed);
    }
}
