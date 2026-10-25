using Microsoft.Win32;

namespace EasyVocal;

/// <summary>
/// "Start with Windows": a value in the current user's Run key (no admin rights needed) that launches
/// <c>"&lt;exe&gt;" --tray</c>, so EasyVocal comes up hidden in the tray at sign-in. The key path and value name can be
/// changed so tests and the smoke test use a throwaway key instead of the real one.
/// </summary>
public sealed class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "EasyVocal";

    /// <summary>The real Run key.</summary>
    public static StartupRegistration Default { get; } = new();

    private readonly string keyPath;
    private readonly string valueName;
    private readonly string? exePath;

    /// <param name="exePath">The program to start; null = the running EasyVocal.exe.</param>
    public StartupRegistration(string keyPath = RunKeyPath, string valueName = DefaultValueName, string? exePath = null)
    {
        this.keyPath = keyPath;
        this.valueName = valueName;
        this.exePath = exePath;
    }

    /// <summary>The line stored in the Run key: the quoted program path and <c>--tray</c>.</summary>
    public static string BuildCommand(string exePath) => "\"" + exePath + "\" --tray";

    /// <summary>The command this instance would store.</summary>
    public string Command => BuildCommand(exePath ?? Environment.ProcessPath
        ?? throw new InvalidOperationException("The path of the running program is unknown."));

    /// <summary>True when the value exists (whatever program it points to).</summary>
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is string { Length: > 0 };
    }

    /// <summary>Adds the value, or removes it (nothing happens when it isn't there).</summary>
    public void Set(bool on)
    {
        if (on)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key.SetValue(valueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// For when the person asked for it: writes the value again if it is missing or points to another place
    /// (the program was moved or updated). Returns true when it wrote.
    /// </summary>
    public bool Refresh()
    {
        string wanted = Command;
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            if (key?.GetValue(valueName) is string current && string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
                return false;
        Set(true);
        return true;
    }
}
