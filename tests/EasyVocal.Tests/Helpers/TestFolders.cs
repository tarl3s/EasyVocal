namespace EasyVocal.Tests.Helpers;

/// <summary>The tests' throwaway folders under %TEMP% (always <c>EV_*</c>) and the repository's root.</summary>
public static class TestFolders
{
    /// <summary>A new empty folder <c>%TEMP%\&lt;prefix&gt;&lt;guid&gt;</c>.</summary>
    public static string New(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes the folder, trying again for a couple of seconds while something still holds a file in it.</summary>
    public static void Delete(string path)
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }

    /// <summary>The repository's root (where EasyVocal.sln is).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EasyVocal.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("EasyVocal.sln not found above " + AppContext.BaseDirectory);
    }
}
