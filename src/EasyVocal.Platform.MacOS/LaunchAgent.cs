using System.Security;
using System.Text;
using EasyVocal.Core.Platform;
using EasyVocal.Platform.Unix;

namespace EasyVocal.Platform.MacOS;

/// <summary>
/// "Open at login": a LaunchAgent (<c>~/Library/LaunchAgents/com.tarl3s.easyvocal.plist</c>, no admin rights needed) that
/// starts EasyVocal with <c>--tray</c> when the person logs in. The folder and program can be changed so the tests use a
/// throwaway folder instead of the real one.
/// </summary>
public sealed class LaunchAgent : IStartupService
{
    public const string Label = "com.tarl3s.easyvocal";
    public const string FileName = Label + ".plist";

    /// <summary>The person's real LaunchAgents folder.</summary>
    public static LaunchAgent Default { get; } = new();

    private readonly string folder;
    private readonly string? programPath;

    /// <param name="folder">Where the plist goes; null = <c>~/Library/LaunchAgents</c>.</param>
    /// <param name="programPath">The program to start; null = the running EasyVocal.</param>
    public LaunchAgent(string? folder = null, string? programPath = null)
    {
        this.folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
        this.programPath = programPath;
    }

    public string PlistPath => Path.Combine(folder, FileName);

    private string Program => programPath ?? Environment.ProcessPath
        ?? throw new InvalidOperationException("The path of the running program is unknown.");

    /// <summary>The plist that starts <paramref name="program"/> hidden in the menu bar at login.</summary>
    public static string BuildPlist(string program)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        sb.Append("    <key>Label</key>\n    <string>").Append(Label).Append("</string>\n");
        sb.Append("    <key>ProgramArguments</key>\n    <array>\n");
        sb.Append("        <string>").Append(SecurityElement.Escape(program)).Append("</string>\n");
        sb.Append("        <string>--tray</string>\n    </array>\n");
        sb.Append("    <key>RunAtLoad</key>\n    <true/>\n");
        sb.Append("    <key>ProcessType</key>\n    <string>Interactive</string>\n");
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    public bool IsEnabled() => File.Exists(PlistPath);

    /// <summary>
    /// Adds the plist (refused, with a sentence why, while EasyVocal runs from a translocated copy: that path is gone at
    /// the next login) or removes it.
    /// </summary>
    public void Set(bool on)
    {
        if (on)
        {
            var program = Program;
            if (InstallLayout.IsTranslocated(program))
                throw new InvalidOperationException("Move EasyVocal to the Applications folder first: it runs from a temporary copy macOS made (" + program + ").");
            Directory.CreateDirectory(folder);
            File.WriteAllText(PlistPath, BuildPlist(program), new UTF8Encoding(false));
        }
        else if (File.Exists(PlistPath)) File.Delete(PlistPath);
    }

    /// <summary>
    /// Writes the plist again when it is missing or starts another place (EasyVocal moved). True when it wrote. A run from a
    /// translocated copy never overwrites a good plist (it would point at a path that vanishes).
    /// </summary>
    public bool Refresh()
    {
        if (InstallLayout.IsTranslocated(Program))
        {
            AppLog.Info("Open at login: EasyVocal runs from a temporary copy macOS made; the login item is left as it is");
            return false;
        }
        var wanted = BuildPlist(Program);
        try
        {
            if (File.Exists(PlistPath) && File.ReadAllText(PlistPath) == wanted) return false;
        }
        catch (IOException) { /* unreadable: write it again */ }
        Set(true);
        return true;
    }
}

/// <summary>macOS has no menu entry to make: the .app in Applications is it (the question is never asked).</summary>
public sealed class NoAppMenuEntry : IShortcutService
{
    public static NoAppMenuEntry Instance { get; } = new();
    public bool Supported => false;
    public string ShortcutPath => "";
    public bool Exists() => false;
    public string? ReadTarget() => null;
    public void Create() => throw new PlatformNotSupportedException("macOS: the app in Applications is the menu entry.");
    public bool Refresh() => false;
}
