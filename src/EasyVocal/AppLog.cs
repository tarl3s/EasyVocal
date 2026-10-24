using System.IO;

namespace EasyVocal;

/// <summary>A small text log (<c>easyvocal.log</c> in the data folder). Never throws.</summary>
public static class AppLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Sync = new();
    private static string? path;

    public static string? FilePath => path;

    public static void Init(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, "easyvocal.log");
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                File.Move(path, path + ".old", overwrite: true);
        }
        catch (Exception)
        {
            path = null; // no log is better than no app
        }
    }

    public static void Info(string message) => Write("info", message);

    public static void Error(string what, Exception? ex) => Write("error", ex == null ? what : what + ": " + ex);

    private static void Write(string level, string message)
    {
        var p = path;
        if (p == null) return;
        try
        {
            lock (Sync) File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        }
        catch (Exception) { /* the disk is full or the file is locked: nothing useful to do */ }
    }
}
