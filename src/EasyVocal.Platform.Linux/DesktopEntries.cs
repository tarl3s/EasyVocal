using System.Text;
using EasyVocal.Core.Platform;
using EasyVocal.Platform.Unix;

namespace EasyVocal.Platform.Linux;

/// <summary>freedesktop.org .desktop files (the Desktop Entry Specification): the applications menu entry and autostart.</summary>
public static class DesktopFile
{
    public const string FileName = "easyvocal.desktop";
    public const string IconName = "easyvocal";

    /// <summary>
    /// One argument of an Exec line: in double quotes when it needs them, with <c>"</c> <c>`</c> <c>$</c> <c>\</c> escaped
    /// by a backslash, then every backslash doubled (the spec's string escaping comes on top), and <c>%</c> as <c>%%</c>.
    /// </summary>
    public static string ExecArgument(string value)
    {
        bool plain = value.Length > 0;
        foreach (char c in value)
            if (!(char.IsLetterOrDigit(c) || c is '/' or '.' or '-' or '_' or '+' or ',' or ':' or '@')) { plain = false; break; }
        if (plain) return value.Replace("%", "%%");
        var quoted = new StringBuilder("\"");
        foreach (char c in value)
        {
            if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(c);
        }
        quoted.Append('"');
        return quoted.ToString().Replace("\\", "\\\\").Replace("%", "%%");
    }

    /// <summary>The program an Exec line starts (its first argument, unescaped), or null.</summary>
    public static string? ProgramOf(string execLine)
    {
        var s = execLine.Replace("%%", "%").Replace("\\\\", "\\");
        if (s.StartsWith('"'))
        {
            var sb = new StringBuilder();
            for (int i = 1; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
                if (s[i] == '"') return sb.ToString();
                sb.Append(s[i]);
            }
            return null;
        }
        int space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    /// <summary>A text value (Name=, Comment=): newlines and tabs escaped as the spec says.</summary>
    private static string Text(string value) =>
        value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");

    /// <param name="program">The EasyVocal program.</param>
    /// <param name="arguments">Its arguments (each made safe for Exec).</param>
    /// <param name="icon">The icon's file, or null for the theme's "easyvocal".</param>
    /// <param name="autostart">For ~/.config/autostart (no menu category; GNOME's "enabled" key).</param>
    public static string Build(string program, IReadOnlyList<string> arguments, string? icon, bool autostart)
    {
        var exec = new StringBuilder(ExecArgument(program));
        foreach (var a in arguments) exec.Append(' ').Append(ExecArgument(a));
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Version=1.0\n");
        sb.Append("Name=EasyVocal\n");
        sb.Append("Comment=").Append(Text("Voice filters and sounds for your calls")).Append('\n');
        sb.Append("Exec=").Append(exec).Append('\n');
        sb.Append("Icon=").Append(Text(icon ?? IconName)).Append('\n');
        sb.Append("Terminal=false\n");
        if (autostart) sb.Append("X-GNOME-Autostart-enabled=true\n");
        else
        {
            sb.Append("Categories=AudioVideo;Audio;\n");
            sb.Append("Keywords=voice;changer;soundboard;microphone;\n");
            sb.Append("StartupWMClass=EasyVocal\n");
        }
        return sb.ToString();
    }

    /// <summary>The Exec= value of a .desktop file's text, or null.</summary>
    public static string? ExecOf(string desktopFile)
    {
        foreach (var raw in desktopFile.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Exec=", StringComparison.Ordinal)) return line["Exec=".Length..];
        }
        return null;
    }

    /// <summary>A PNG's width from its IHDR chunk (bytes 16–19, big-endian); 256 when it can't be read.</summary>
    public static int PngWidth(byte[] png) =>
        png.Length >= 24 ? (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) is var w and > 0 and <= 4096 ? w : 256 : 256;

    /// <summary>The largest PNG inside an .ico file (EasyVocal's icon is drawn as PNGs), or null.</summary>
    public static byte[]? LargestPngInIco(byte[] ico)
    {
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1) return null;
        int count = BitConverter.ToUInt16(ico, 4);
        byte[]? best = null;
        int bestSize = -1;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + 16 * i;
            if (entry + 16 > ico.Length) break;
            int width = ico[entry] == 0 ? 256 : ico[entry];
            int length = BitConverter.ToInt32(ico, entry + 8), offset = BitConverter.ToInt32(ico, entry + 12);
            if (offset < 0 || length < 8 || offset + length > ico.Length) continue;
            if (ico[offset] != 0x89 || ico[offset + 1] != (byte)'P' || ico[offset + 2] != (byte)'N' || ico[offset + 3] != (byte)'G') continue;
            if (width <= bestSize) continue;
            bestSize = width;
            best = ico.AsSpan(offset, length).ToArray();
        }
        return best;
    }
}

/// <summary>
/// "Start at login": <c>~/.config/autostart/easyvocal.desktop</c> (XDG autostart, honoured by GNOME, KDE, Xfce…) that
/// starts EasyVocal with <c>--tray</c>. The folder and program can be changed so the tests use throwaway ones.
/// </summary>
public sealed class AutostartEntry : IStartupService
{
    public static AutostartEntry Default { get; } = new();

    private readonly string folder;
    private readonly string? programPath;

    /// <param name="folder">null = <c>$XDG_CONFIG_HOME/autostart</c> (<c>~/.config/autostart</c>).</param>
    public AutostartEntry(string? folder = null, string? programPath = null)
    {
        this.folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");
        this.programPath = programPath;
    }

    public string EntryPath => Path.Combine(folder, DesktopFile.FileName);

    private string Program => programPath ?? Environment.ProcessPath ?? throw new InvalidOperationException("The path of the running program is unknown.");

    public string Wanted() => DesktopFile.Build(Program, new[] { "--tray" }, null, autostart: true);

    public bool IsEnabled() => File.Exists(EntryPath);

    public void Set(bool on)
    {
        if (on)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(EntryPath, Wanted(), new UTF8Encoding(false));
        }
        else if (File.Exists(EntryPath)) File.Delete(EntryPath);
    }

    public bool Refresh()
    {
        var wanted = Wanted();
        try
        {
            if (File.Exists(EntryPath) && File.ReadAllText(EntryPath) == wanted) return false;
        }
        catch (IOException) { /* unreadable: write it again */ }
        Set(true);
        return true;
    }
}

/// <summary>
/// The applications menu entry: <c>~/.local/share/applications/easyvocal.desktop</c> with EasyVocal's icon in
/// <c>~/.local/share/icons/hicolor/256x256/apps/easyvocal.png</c>. Asked once, like Windows' Start menu shortcut.
/// </summary>
public sealed class AppMenuEntry : IShortcutService
{
    private readonly string dataHome;
    private readonly string? programPath;
    private readonly Func<byte[]?>? icon;

    /// <param name="dataHome">null = <c>$XDG_DATA_HOME</c> (<c>~/.local/share</c>).</param>
    /// <param name="icon">EasyVocal's icon as .ico bytes (its largest PNG is used); null = none.</param>
    public AppMenuEntry(string? dataHome = null, string? programPath = null, Func<byte[]?>? icon = null)
    {
        this.dataHome = dataHome ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        this.programPath = programPath;
        this.icon = icon;
    }

    public bool Supported => true;

    public string ShortcutPath => Path.Combine(dataHome, "applications", DesktopFile.FileName);

    /// <summary>Where the icon goes: the hicolor theme's folder for its size (<c>256x256</c> for a 256 px PNG).</summary>
    public string IconPath => IconPathFor(IconPng() is { } png ? DesktopFile.PngWidth(png) : 256);

    public string IconPathFor(int size) =>
        Path.Combine(dataHome, "icons", "hicolor", size + "x" + size, "apps", DesktopFile.IconName + ".png");

    private byte[]? IconPng() => icon?.Invoke() is { } ico ? DesktopFile.LargestPngInIco(ico) : null;

    private string Program => programPath ?? Environment.ProcessPath ?? throw new InvalidOperationException("The path of the running program is unknown.");

    public bool Exists() => File.Exists(ShortcutPath);

    public string? ReadTarget()
    {
        try
        {
            if (!Exists()) return null;
            return DesktopFile.ExecOf(File.ReadAllText(ShortcutPath)) is { } exec ? DesktopFile.ProgramOf(exec) : null;
        }
        catch (Exception) { return null; }
    }

    public void Create()
    {
        string? iconFile = null;
        if (IconPng() is { } png)
        {
            var path = IconPathFor(DesktopFile.PngWidth(png));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, png);
            iconFile = path;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        File.WriteAllText(ShortcutPath, DesktopFile.Build(Program, Array.Empty<string>(), iconFile, autostart: false), new UTF8Encoding(false));
    }

    public bool Refresh()
    {
        try
        {
            if (ReadTarget() != Program || (icon != null && !File.Exists(IconPath))) Create();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Couldn't add EasyVocal to the applications menu", ex);
            return false;
        }
    }
}

/// <summary>Whether the desktop shows tray icons (a StatusNotifierItem host: KDE, or GNOME with the AppIndicator extension).</summary>
public static class TrayHost
{
    public static IReadOnlyList<string> DbusArguments { get; } = new[]
    {
        "--session", "--print-reply", "--dest=org.freedesktop.DBus", "/org/freedesktop/DBus",
        "org.freedesktop.DBus.NameHasOwner", "string:org.kde.StatusNotifierWatcher",
    };

    /// <summary>True when the session bus has a StatusNotifierWatcher; when it can't be asked (no dbus-send), true.</summary>
    public static bool Present(ICommandRunner? runner = null)
    {
        runner ??= CommandRunner.Instance;
        var tool = runner.Find("dbus-send", "/usr/bin");
        if (tool == null) return true;
        var result = runner.RunAsync(tool, DbusArguments, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        if (!result.Ok) return true; // no answer is not a "no"
        return result.Output.Contains("boolean true", StringComparison.Ordinal);
    }
}
