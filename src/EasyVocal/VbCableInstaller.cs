using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace EasyVocal;

/// <summary>How <see cref="VbCableInstaller.DownloadAndRunAsync"/> ended.</summary>
public enum InstallStatus
{
    /// <summary>The installer ran and was closed (whether the driver is there is up to Windows: the window checks the devices).</summary>
    Installed,
    /// <summary>The person said no to the Windows permission prompt.</summary>
    DeclinedPermission,
    /// <summary>Stopped by the caller (the window was closed); an installer already running is left alone.</summary>
    Cancelled,
    /// <summary>The downloaded installer is not signed by VB-Audio (or the signature is not trusted): it was not run.</summary>
    NotVerified,
    /// <summary>Download, unpacking or starting failed; <see cref="InstallResult.Detail"/> says why (for the log).</summary>
    Failed,
}

/// <summary>The outcome of an install attempt.</summary>
public readonly record struct InstallResult(InstallStatus Status, string? Detail = null);

/// <summary>
/// Gets VB-CABLE (the free virtual microphone EasyVocal sends the voice to) from its official site: downloads the
/// driver pack, unpacks it, checks the installer's signature and starts it (it asks Windows for permission).
/// EasyVocal does not bundle the driver (VB-Audio's licence). Nothing here is called until the person presses Install.
/// </summary>
public static class VbCableInstaller
{
    public const string PageUrl = "https://vb-audio.com/Cable/";
    public const string ZipUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";

    private const long MaxZipBytes = 50L * 1024 * 1024; // the real pack is about 1.3 MB
    private const int UacCancelled = 1223; // ERROR_CANCELLED: the person pressed No on the permission prompt
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    /// <summary>Where the packs are unpacked, one folder per attempt: <c>%TEMP%\EasyVocal_VBCABLE\&lt;id&gt;</c>.</summary>
    public static string WorkFolder => Path.Combine(Path.GetTempPath(), "EasyVocal_VBCABLE");

    /// <summary>
    /// Downloads, unpacks, verifies and runs the installer (with the Windows permission prompt), and completes when the
    /// installer closes. <paramref name="progress"/> gets 0..1: the download fills most of it and 1 means "the installer is starting".
    /// </summary>
    public static async Task<InstallResult> DownloadAndRunAsync(IProgress<double> progress, CancellationToken ct)
    {
        CleanStale(WorkFolder, StaleAfter);
        var runFolder = Path.Combine(WorkFolder, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(runFolder);
            var zip = Path.Combine(runFolder, "VBCABLE_Driver_Pack.zip");
            await DownloadAsync(ZipUrl, zip, progress, ct).ConfigureAwait(false);
            return await InstallFromZipAsync(zip, runFolder, progress, ct, Authenticode.Check, RunElevatedAsync).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(runFolder); // cancelled while downloading: no installer is running
            return new InstallResult(InstallStatus.Cancelled);
        }
        catch (Exception ex)
        {
            TryDelete(runFolder); // nothing was started yet
            return new InstallResult(InstallStatus.Failed, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Everything after the download: unpack, find the installer, verify its signature, run it. The folder is removed
    /// only when no installer can still be using it: never after a cancellation (the elevated installer may still be
    /// running and reading its driver files); a later run cleans stale folders.
    /// </summary>
    internal static async Task<InstallResult> InstallFromZipAsync(string zipPath, string runFolder, IProgress<double> progress,
        CancellationToken ct, Func<string, SignatureCheck> verify, Func<string, CancellationToken, Task> launch)
    {
        bool keepFolder = false;
        try
        {
            var packFolder = Path.Combine(runFolder, "pack");
            ExtractZip(zipPath, packFolder);
            var exe = FindSetupExe(packFolder, Environment.Is64BitOperatingSystem)
                ?? throw new FileNotFoundException("The VB-CABLE installer is not in the download.");
            var check = verify(exe);
            if (check != SignatureCheck.Ok)
                return new InstallResult(InstallStatus.NotVerified, "Signature check: " + check);
            progress.Report(1);
            await launch(exe, ct).ConfigureAwait(false);
            return new InstallResult(InstallStatus.Installed);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == UacCancelled)
        {
            return new InstallResult(InstallStatus.DeclinedPermission);
        }
        catch (OperationCanceledException)
        {
            keepFolder = true;
            return new InstallResult(InstallStatus.Cancelled);
        }
        catch (Exception ex)
        {
            return new InstallResult(InstallStatus.Failed, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (!keepFolder) TryDelete(runFolder);
        }
    }

    /// <summary>Removes attempt folders older than <paramref name="olderThan"/> (left behind by a cancelled run), never a fresh one.</summary>
    internal static void CleanStale(string root, TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > olderThan) TryDelete(dir);
        }
        catch (Exception) { /* a folder in use: the next run tries again */ }
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
