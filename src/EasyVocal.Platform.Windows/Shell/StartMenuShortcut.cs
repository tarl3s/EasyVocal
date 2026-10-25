using System.IO;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Windows;

/// <summary>
/// The "EasyVocal" entry in the current user's Start menu (a .lnk in their Programs folder; no admin rights needed).
/// The folder can be changed so tests use a throwaway one instead of the real Start menu.
/// </summary>
public sealed class StartMenuShortcut : IShortcutService
{
    public const string FileName = "EasyVocal.lnk";

    /// <summary>The real Start menu.</summary>
    public static StartMenuShortcut Default { get; } = new();

    private readonly string folder;
    private readonly string? exePath;

    /// <param name="folder">Where the shortcut goes; null = the current user's Start menu Programs folder.</param>
    /// <param name="exePath">The program it opens; null = the running EasyVocal.exe.</param>
    public StartMenuShortcut(string? folder = null, string? exePath = null)
    {
        this.folder = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        this.exePath = exePath;
    }

    /// <summary>Windows always has a Start menu.</summary>
    public bool Supported => true;

    public string ShortcutPath => Path.Combine(folder, FileName);

    private string Target => exePath ?? Environment.ProcessPath
        ?? throw new InvalidOperationException("The path of the running program is unknown.");

    public bool Exists() => File.Exists(ShortcutPath);

    /// <summary>The program the shortcut opens, or null when there is none (or it can't be read).</summary>
    public string? ReadTarget()
    {
        if (!Exists()) return null;
        try
        {
            dynamic shell = CreateShell();
            dynamic link = shell.CreateShortcut(ShortcutPath);
            return (string)link.TargetPath;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Creates (or rewrites) the shortcut to open EasyVocal.</summary>
    public void Create()
    {
        Directory.CreateDirectory(folder);
        var target = Target;
        dynamic shell = CreateShell();
        dynamic link = shell.CreateShortcut(ShortcutPath);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
        link.IconLocation = target + ",0";
        link.Description = "EasyVocal — voice filters and sounds for your calls";
        link.Save();
    }

    /// <summary>For when the person asked for it: writes the shortcut again if it is missing or opens another place
    /// (EasyVocal.exe was moved). Never throws: returns the error, or null when all is well.</summary>
    public Exception? Refresh()
    {
        try
        {
            if (!string.Equals(ReadTarget(), Target, StringComparison.OrdinalIgnoreCase)) Create();
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    private static object CreateShell() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available."))!;
}
