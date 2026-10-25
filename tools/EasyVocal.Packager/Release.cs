namespace EasyVocal.Packager;

/// <summary>
/// The release downloads, by runtime identifier. The names are fixed: the README's download buttons point at
/// <c>releases/latest/download/&lt;name&gt;</c> and the updater of each platform looks for its own (the tests check both).
/// </summary>
public static class Release
{
    public const string WindowsX64 = "EasyVocal-win-x64.zip";
    public const string MacArm64 = "EasyVocal-macos-arm64.zip";
    public const string MacX64 = "EasyVocal-macos-x64.zip";
    public const string LinuxX64 = "EasyVocal-linux-x64.tar.gz";

    /// <summary>GitHub's limit is 2 GB; ours keeps downloads small (the updater refuses more than 200 MB).</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    public static readonly IReadOnlyList<string> Rids = new[] { "win-x64", "osx-arm64", "osx-x64", "linux-x64" };

    public static string AssetName(string rid) => rid switch
    {
        "win-x64" => WindowsX64,
        "osx-arm64" => MacArm64,
        "osx-x64" => MacX64,
        "linux-x64" => LinuxX64,
        _ => throw new ArgumentException("No release for " + rid, nameof(rid)),
    };

    /// <summary>What was packed: the archive, its size and a line about the program (architecture, signature).</summary>
    public sealed record Packed(string Archive, long Bytes, string Note);

    /// <param name="publishFolder">A <c>dotnet publish -r &lt;rid&gt;</c> folder.</param>
    /// <param name="iconPngs">The icon as PNGs (macOS: every size for the .icns; Linux: the 256 px one is used).</param>
    public static Packed Pack(string rid, string publishFolder, string outFolder, string version, IReadOnlyList<byte[]> iconPngs)
    {
        Directory.CreateDirectory(outFolder);
        var archive = Path.Combine(outFolder, AssetName(rid));
        string note;
        switch (rid)
        {
            case "win-x64":
            {
                var exe = Need(publishFolder, "EasyVocal.exe");
                var head = new byte[2];
                using (var f = File.OpenRead(exe)) f.ReadExactly(head);
                if (head[0] != 'M' || head[1] != 'Z') throw new InvalidDataException("EasyVocal.exe is not a Windows program");
                var files = Archives.FilesIn(publishFolder, "").Select(f => ArchiveFile.FromFile(f.Path, f.Source, Archives.Regular)).ToList();
                Archives.WriteZip(archive, Manifest.Add(files, "", Manifest.FileName), unix: false);
                note = "EasyVocal.exe";
                break;
            }
            case "osx-arm64" or "osx-x64":
            {
                var program = Need(publishFolder, MacApp.Executable);
                Need(publishFolder, "libportaudio.dylib");
                var macho = MacApp.ReadMachO(program) ?? throw new InvalidDataException("EasyVocal is not a macOS program");
                string want = rid == "osx-arm64" ? "arm64" : "x64";
                if (macho.Architecture != want) throw new InvalidDataException($"EasyVocal is {macho.Architecture}, not {want}");
                Archives.WriteZip(archive, Manifest.Add(MacApp.Bundle(publishFolder, version, MacApp.Icns(iconPngs)), MacApp.BundleName + "/", "Contents/Resources/" + Manifest.FileName), unix: true);
                note = $"{MacApp.BundleName} ({macho.Architecture}, {(macho.Signed ? "signed" : "no code signature")})";
                break;
            }
            case "linux-x64":
            {
                var program = Need(publishFolder, LinuxApp.Executable);
                Need(publishFolder, "libportaudio.so");
                if (!LinuxApp.IsElf(program)) throw new InvalidDataException("EasyVocal is not a Linux program");
                var icon = iconPngs.FirstOrDefault(p => MacApp.PngWidth(p) == 256) ?? iconPngs.OrderByDescending(MacApp.PngWidth).First();
                Archives.WriteTarGz(archive, Manifest.Add(LinuxApp.Files(publishFolder, icon), LinuxApp.Folder + "/", Manifest.FileName));
                note = LinuxApp.Folder + "/";
                break;
            }
            default:
                throw new ArgumentException("No release for " + rid, nameof(rid));
        }
        long bytes = new FileInfo(archive).Length;
        if (bytes > MaxBytes) throw new InvalidDataException($"{Path.GetFileName(archive)} is {bytes / 1048576.0:N1} MB: the limit is {MaxBytes / 1048576} MB");
        return new Packed(archive, bytes, note);
    }

    private static string Need(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path)) throw new FileNotFoundException($"The publish folder has no {name}: {folder}", path);
        return path;
    }
}
