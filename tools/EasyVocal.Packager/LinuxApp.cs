using System.Text;

namespace EasyVocal.Packager;

/// <summary>
/// The Linux download's <c>EasyVocal/</c> folder: the program (0755) and its native libraries (PortAudio among them),
/// <c>easyvocal.png</c>, an <c>easyvocal.desktop</c> template and <c>install.sh</c>, which copies the folder to
/// <c>~/.local/share/EasyVocal</c> and adds it to the applications menu (the same files the app's own "add to the
/// applications menu" writes, so the two agree).
/// </summary>
public static class LinuxApp
{
    public const string Folder = "EasyVocal";
    public const string Executable = "EasyVocal";
    public const string DesktopFileName = "easyvocal.desktop";
    public const string IconFileName = "easyvocal.png";
    public const string InstallScriptName = "install.sh";

    /// <summary>The menu entry; install.sh fills in the program's real path on the Exec line.</summary>
    public static string DesktopTemplate() =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Version=1.0\n" +
        "Name=EasyVocal\n" +
        "Comment=Voice filters and sounds for your calls\n" +
        "Exec=EasyVocal\n" +
        "Icon=easyvocal\n" +
        "Terminal=false\n" +
        "Categories=AudioVideo;Audio;\n" +
        "Keywords=voice;changer;soundboard;microphone;\n" +
        "StartupWMClass=EasyVocal\n";

    public static string InstallScript() => """
#!/bin/sh
# Installs EasyVocal for you (no admin rights): copies this folder to ~/.local/share/EasyVocal and adds EasyVocal to
# the applications menu. Run it again to update; delete ~/.local/share/EasyVocal and
# ~/.local/share/applications/easyvocal.desktop to remove it.
set -e
SRC=$(cd "$(dirname "$0")" && pwd)
DATA=${XDG_DATA_HOME:-"$HOME/.local/share"}
DEST="$DATA/EasyVocal"
if [ "$SRC" != "$DEST" ]; then
    mkdir -p "$DATA"
    rm -rf "$DEST.new"
    cp -R "$SRC" "$DEST.new"
    rm -rf "$DEST"
    mv "$DEST.new" "$DEST"
fi
chmod +x "$DEST/EasyVocal" "$DEST/install.sh"
mkdir -p "$DATA/applications" "$DATA/icons/hicolor/256x256/apps"
cp "$DEST/easyvocal.png" "$DATA/icons/hicolor/256x256/apps/easyvocal.png"
# The Exec line: the program in double quotes, escaped as the .desktop format says (\ " ` $ get a backslash, then
# every backslash is doubled; % becomes %%).
EXEC=$(printf '%s' "$DEST/EasyVocal" | sed -e 's/\\/\\\\\\\\/g' -e 's/["`$]/\\\\&/g' -e 's/%/%%/g')
{ grep -v '^Exec=' "$DEST/easyvocal.desktop"; printf 'Exec="%s"\n' "$EXEC"; } > "$DATA/applications/easyvocal.desktop"
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DATA/applications" >/dev/null 2>&1 || true
fi
echo "EasyVocal is installed in $DEST and in your applications menu."
echo "Open it from the menu, or run: \"$DEST/EasyVocal\""

""";

    /// <summary>The folder's files from a <c>dotnet publish -r linux-x64</c> folder.</summary>
    public static IReadOnlyList<ArchiveFile> Files(string publishFolder, byte[] iconPng)
    {
        var files = new List<ArchiveFile>();
        foreach (var (path, source) in Archives.FilesIn(publishFolder, Folder + "/"))
        {
            var name = Path.GetFileName(path);
            bool exec = name == Executable || name.EndsWith(".so", StringComparison.Ordinal) || name.Contains(".so.", StringComparison.Ordinal);
            files.Add(ArchiveFile.FromFile(path, source, exec ? Archives.Executable : Archives.Regular));
        }
        files.Add(ArchiveFile.FromBytes(Folder + "/" + IconFileName, iconPng, Archives.Regular));
        files.Add(ArchiveFile.FromBytes(Folder + "/" + DesktopFileName, Encoding.UTF8.GetBytes(DesktopTemplate()), Archives.Regular));
        files.Add(ArchiveFile.FromBytes(Folder + "/" + InstallScriptName, Encoding.UTF8.GetBytes(InstallScript().Replace("\r\n", "\n")), Archives.Executable));
        return files;
    }

    public static bool IsElf(string path)
    {
        var head = new byte[4];
        using var f = File.OpenRead(path);
        return f.Read(head, 0, 4) == 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F';
    }
}
