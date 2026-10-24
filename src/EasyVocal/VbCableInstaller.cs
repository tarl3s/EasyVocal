using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace EasyVocal;

/// <summary>
/// Gets VB-CABLE (the free virtual microphone EasyVocal sends the voice to) from its official site: downloads the
/// driver pack, unpacks it and starts its own installer, which asks Windows for permission. EasyVocal does not
/// bundle the driver (VB-Audio's licence). Nothing here is called until the person presses Install.
/// </summary>
public static class VbCableInstaller
{
    public const string PageUrl = "https://vb-audio.com/Cable/";
    public const string ZipUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";

    private const long MaxZipBytes = 50L * 1024 * 1024; // the real pack is about 1.3 MB
    private const int UacCancelled = 1223; // ERROR_CANCELLED: the person pressed No on the permission prompt

    /// <summary>Why the last <see cref="DownloadAndRunAsync"/> returned false (for the log).</summary>
    public static string? LastError { get; private set; }

    /// <summary>The person said no to the permission prompt (not a failure to apologise for).</summary>
    public static bool UserCancelled { get; private set; }

    /// <summary>Where the pack is unpacked: <c>%TEMP%\EasyVocal_VBCABLE</c>.</summary>
    public static string WorkFolder => Path.Combine(Path.GetTempPath(), "EasyVocal_VBCABLE");

    /// <summary>
    /// Downloads, unpacks and runs the installer (with the Windows permission prompt), and completes when the installer closes.
    /// <paramref name="progress"/> gets 0..1: the download fills most of it and 1 means "the installer is starting".
    /// False on any failure (see <see cref="LastError"/>), or when the person cancelled (<see cref="UserCancelled"/>).
    /// </summary>
    public static async Task<bool> DownloadAndRunAsync(IProgress<double> progress, CancellationToken ct)
    {
        LastError = null;
        UserCancelled = false;
        var folder = WorkFolder;
        try
        {
            TryDelete(folder);
            Directory.CreateDirectory(folder);
            var zip = Path.Combine(folder, "VBCABLE_Driver_Pack.zip");
            await DownloadAsync(ZipUrl, zip, progress, ct).ConfigureAwait(false);
            ExtractZip(zip, Path.Combine(folder, "pack"));
            var exe = FindSetupExe(Path.Combine(folder, "pack"), Environment.Is64BitOperatingSystem)
                ?? throw new FileNotFoundException("The VB-CABLE installer is not in the download.");
            progress.Report(1);
            await RunElevatedAsync(exe, ct).ConfigureAwait(false);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == UacCancelled)
        {
            UserCancelled = true;
            LastError = "The permission prompt was declined.";
            return false;
        }
        catch (OperationCanceledException)
        {
            LastError = "Cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            TryDelete(folder);
        }
    }

    /// <summary>Opens the official download page in the browser.</summary>
    public static void OpenOfficialPage()
    {
        try { Process.Start(new ProcessStartInfo(PageUrl) { UseShellExecute = true }); }
        catch (Exception) { /* no browser: the person is told the address in the window */ }
    }

    /// <summary>The installer in an unpacked pack: <c>VBCABLE_Setup_x64.exe</c> (<c>VBCABLE_Setup.exe</c> on 32-bit Windows), or null.</summary>
    internal static string? FindSetupExe(string folder, bool is64Bit)
    {
        if (!Directory.Exists(folder)) return null;
        var name = is64Bit ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe";
        foreach (var file in Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories))
            if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase)) return file;
        return null;
    }

    /// <summary>Unpacks a zip into <paramref name="folder"/>; an entry that would land outside it is an error.</summary>
    internal static void ExtractZip(string zipPath, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download contains a file outside its folder.");
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target); // a folder entry
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static async Task DownloadAsync(string url, string path, IProgress<double> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EasyVocal");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        if (total > MaxZipBytes) throw new InvalidDataException("The download is bigger than expected.");

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(path);
        var buffer = new byte[16 * 1024];
        long done = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            if (done > MaxZipBytes) throw new InvalidDataException("The download is bigger than expected.");
            if (total > 0) progress.Report(0.9 * done / total.Value);
        }
    }

    /// <summary>Starts the installer with the "run as administrator" prompt and waits until it closes (or <paramref name="ct"/> fires).</summary>
    private static async Task RunElevatedAsync(string exe, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        }) ?? throw new InvalidOperationException("The installer did not start.");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception) { /* a file still in use: the next run cleans it */ }
    }
}
