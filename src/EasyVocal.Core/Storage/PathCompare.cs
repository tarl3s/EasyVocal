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
