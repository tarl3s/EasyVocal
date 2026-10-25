using System;

namespace EasyVocal.Core.Storage;

/// <summary>How paths compare here: case-sensitive on Linux, case-insensitive on Windows and macOS (their usual disks).</summary>
public static class PathCompare
{
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>The same path (as written; no normalisation).</summary>
    public static bool Same(string? a, string? b) => string.Equals(a, b, Comparison);

    /// <summary><paramref name="path"/> starts with <paramref name="rootWithSeparator"/> (end it with a separator).</summary>
    public static bool IsUnder(string path, string rootWithSeparator) => path.StartsWith(rootWithSeparator, Comparison);
}

/// <summary>
/// What a plain file name of the Sounds folder is (no folder part, nothing that leaves it), by this system's rules:
/// Windows refuses <c>\ / : * ? " &lt; &gt; |</c> and control characters; macOS and Linux only <c>/</c> and NUL (a
/// name with <c>:</c> or <c>\</c> is fine there). Names compare with <see cref="PathCompare.Same"/>.
/// </summary>
public static class FileNames
{
    private static readonly char[] WindowsInvalid = BuildWindowsInvalid();
    private static readonly char[] UnixInvalid = { '/', '\0' };

    private static char[] BuildWindowsInvalid()
    {
        var chars = new List<char> { '"', '<', '>', '|', ':', '*', '?', '\\', '/' };
        for (char c = '\0'; c < ' '; c++) chars.Add(c);
        return chars.ToArray();
    }

    /// <summary>The characters a file name can't have here (<paramref name="windows"/>: Windows' rules, else macOS/Linux').</summary>
    public static char[] InvalidChars(bool windows) => windows ? WindowsInvalid : UnixInvalid;

    /// <summary>A plain file name by this system's rules.</summary>
    public static bool IsPlain([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? name) => IsPlain(name, OperatingSystem.IsWindows());

    /// <summary>A plain file name by Windows' rules (<paramref name="windows"/>) or macOS/Linux' ones; never "." or "..".</summary>
    public static bool IsPlain([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? name, bool windows) =>
        !string.IsNullOrWhiteSpace(name) && name != "." && name != ".." && name.IndexOfAny(InvalidChars(windows)) < 0;
}
