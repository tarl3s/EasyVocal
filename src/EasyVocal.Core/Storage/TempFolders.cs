using System;
using System.IO;

namespace EasyVocal.Core.Storage;

/// <summary>The per-attempt folders under %TEMP% (updates, the virtual-mic installer).</summary>
public static class TempFolders
{
    /// <summary>Removes attempt folders older than <paramref name="olderThan"/> (left behind by a cancelled run), never a fresh one.</summary>
    public static void CleanStale(string root, TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > olderThan) TryDelete(dir);
        }
        catch (Exception) { /* a folder in use: the next run tries again */ }
    }

    /// <summary>Deletes a folder and everything in it. Never throws.</summary>
    public static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception) { /* a file still in use: the next run cleans it */ }
    }
}
