using System;
using System.IO;

namespace EasyVocal.Core.Storage;

/// <summary>Deleting what EasyVocal itself left: a file or a whole folder, never an exception.</summary>
public static class Cleanup
{
    /// <summary>
    /// Deletes a file, or a folder with everything in it (a .app bundle, an attempt folder). Never throws: something
    /// still in use stays, and the next cleanup gets it.
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception) { /* in use or gone */ }
    }
}

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
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > olderThan) Cleanup.TryDelete(dir);
        }
        catch (Exception) { /* a folder in use: the next run tries again */ }
    }
}
