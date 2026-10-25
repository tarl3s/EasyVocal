using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using EasyVocal.Core.Update;
using EasyVocal.Platform.Windows;

namespace EasyVocal.Tests;

/// <summary>The updater on Windows, without the network: the release zip, the helper script (run for real on
/// throwaway folders whose names have spaces, accents and quotes) and the service with a fake source and launcher.</summary>
public class UpdaterTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "EV_Test_Update_" + Guid.NewGuid().ToString("N"));

    public UpdaterTests() => Directory.CreateDirectory(temp);

    public void Dispose()
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(temp); attempt++)
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }

    private static readonly WindowsUpdaterPlatform Win = WindowsUpdaterPlatform.Instance;

    private static readonly byte[] FakeExe = Encoding.ASCII.GetBytes("MZ this stands in for EasyVocal.exe");

    // ---------- which file (Windows' names) ----------

    private static ReleaseInfo WithAssets(params string[] names) => new(new AppVersion(0, 3, 0), "v0.3.0", "",
        names.Select(n => new ReleaseAsset(n, "https://example.com/" + n, 10)).ToList(), false, false, UpdateRules.PageUrl);

    /// <summary>GitHub's answer for a release (same as UpdateRulesTests').</summary>
    private static string ReleaseJson(string tag = "v0.3.0", string extra = "", string? assets = null) =>
        "{ \"tag_name\": \"" + tag + "\", \"name\": \"EasyVocal " + tag + "\", \"body\": \"## New\\n- Updates itself\"," +
        " \"html_url\": \"https://github.com/tarl3s/EasyVocal/releases/tag/" + tag + "\"," + extra +
        " \"assets\": " + (assets ?? "[{ \"name\": \"EasyVocal-win-x64.zip\", \"size\": 70000000, \"browser_download_url\": \"https://github.com/tarl3s/EasyVocal/releases/download/" + tag + "/EasyVocal-win-x64.zip\" }]") + " }";

    private static ReleaseAsset? Pick(ReleaseInfo r, Architecture os) => UpdateRules.PickAsset(r, Win.AssetNames(os));

    [Fact]
    public void PickAsset_TakesThisPlatformsZip()
    {
        var r = WithAssets("EasyVocal-win-x64.zip.sha256", "Source code.zip", "easyvocal-WIN-x64.ZIP", "EasyVocal-win-arm64.zip");
        Assert.Equal("easyvocal-WIN-x64.ZIP", Pick(r, Architecture.X64)!.Name);
        Assert.Equal("EasyVocal-win-arm64.zip", Pick(r, Architecture.Arm64)!.Name);
        Assert.Equal("EasyVocal-win-x64.zip", Pick(WithAssets("EasyVocal-win-x64.zip"), Architecture.Arm64)!.Name); // x64 runs on ARM
        Assert.Null(Pick(r, Architecture.X86));
        Assert.Null(Pick(WithAssets("EasyVocal-win-x64.zip.sha256", "EasyVocal.exe"), Architecture.X64));
    }

    // ---------- the zip ----------

    private string MakeZip(string name, params (string Entry, byte[] Data)[] files)
    {
        var path = Path.Combine(temp, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in files)
        {
            using var s = zip.CreateEntry(entry).Open();
            s.Write(data);
        }
        return path;
    }

    [Fact]
    public void ExtractExe_TakesTheExeOut()
    {
        var zip = MakeZip("ok.zip", ("EasyVocal.exe", FakeExe), ("README.txt", new byte[] { 1 }));
        var exe = UpdatePackage.ExtractExe(zip, Path.Combine(temp, "out"), Win);
        Assert.Equal(Path.Combine(temp, "out", "EasyVocal.exe"), exe);
        Assert.Equal(FakeExe, File.ReadAllBytes(exe));
        Assert.False(File.Exists(Path.Combine(temp, "out", "README.txt"))); // only the exe is unpacked

        var nested = MakeZip("nested.zip", ("EasyVocal-0.3.0/EasyVocal.exe", FakeExe));
        Assert.Equal(FakeExe, File.ReadAllBytes(UpdatePackage.ExtractExe(nested, Path.Combine(temp, "out2"), Win)));
    }

    [Fact]
    public void ExtractExe_RefusesBadZips()
    {
        void Fails(UpdateProblem problem, string zip) =>
            Assert.Equal(problem, Assert.Throws<UpdateException>(() => UpdatePackage.ExtractExe(zip, Path.Combine(temp, "x"), Win)).Problem);

        Fails(UpdateProblem.MissingExe, MakeZip("none.zip", ("Other.exe", FakeExe)));
        Fails(UpdateProblem.MissingExe, MakeZip("deep.zip", ("a/b/EasyVocal.exe", FakeExe)));
        Fails(UpdateProblem.MissingExe, MakeZip("empty.zip"));
        Fails(UpdateProblem.UnsafeZip, MakeZip("slip.zip", ("EasyVocal.exe", FakeExe), ("../../evil.txt", new byte[] { 1 })));
        Fails(UpdateProblem.UnsafeZip, MakeZip("slip2.zip", ("..\\EasyVocal.exe", FakeExe)));
        Fails(UpdateProblem.NotAnExe, MakeZip("text.zip", ("EasyVocal.exe", Encoding.ASCII.GetBytes("<html>404</html>"))));
        var notZip = Path.Combine(temp, "page.zip");
        File.WriteAllText(notZip, "<html>rate limited</html>");
        Fails(UpdateProblem.NotAZip, notZip);

        Assert.False(File.Exists(Path.Combine(temp, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(temp)!, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(temp, "x", "EasyVocal.exe"))); // the non-program was removed again
    }

    // ---------- the helper script ----------

    [Fact]
    public void Quote_DoublesEverySingleQuote()
    {
        Assert.Equal("'C:\\a b\\x'", HelperScript.Quote("C:\\a b\\x"));
        Assert.Equal("'it''s'", HelperScript.Quote("it's"));
        Assert.Equal("'a\u2019\u2019b\u2018\u2018'", HelperScript.Quote("a\u2019b\u2018")); // PowerShell ends a string on these too
        Assert.Equal("'$env:x `n'", HelperScript.Quote("$env:x `n")); // single quotes keep $ and ` literal
        Assert.Equal("--data \"C:\\Área de Trabalho\\d\"", HelperScript.RelaunchArguments("C:\\Área de Trabalho\\d\\"));
        Assert.Equal("", HelperScript.RelaunchArguments(null));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int count);

    /// <summary>The arguments a program gets from <paramref name="arguments"/> (Windows' own parser).</summary>
    private static string[] ParsedByWindows(string arguments)
    {
        var argv = CommandLineToArgvW("EasyVocal.exe " + arguments, out int count);
        try
        {
            var result = new string[count - 1];
            for (int i = 1; i < count; i++) result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(argv); // LocalAlloc'ed: LocalFree and FreeHGlobal are the same
        }
    }

    [Theory]
    [InlineData("D:\\", "D:\\")]
    [InlineData("C:\\data\\", "C:\\data")]
    [InlineData("C:\\Área de Trabalho\\my data", "C:\\Área de Trabalho\\my data")]
    [InlineData("\\\\server\\share\\x y\\", "\\\\server\\share\\x y")]
    public void RelaunchArguments_GiveTheDataFolderBack(string folder, string expected)
    {
        Assert.Equal(new[] { "--data", expected }, ParsedByWindows(HelperScript.RelaunchArguments(folder)));
    }

    [Theory]
    [InlineData("a\\")]
    [InlineData("a\\\\")]
    [InlineData("say \"hi\"")]
    [InlineData("x\\\"y")]
    [InlineData("")]
    public void QuoteArgument_IsReadBackExactly(string value)
    {
        Assert.Equal(new[] { value }, ParsedByWindows(HelperScript.QuoteArgument(value)));
    }

    [Fact]
    public void Build_PutsThePathsInAsLiterals()
    {
        var plan = new HelperPlan(1234, @"C:\Users\Zé\OneDrive\Área de Trabalho\it's mine\EasyVocal.exe",
            @"C:\Temp\EasyVocal_update\abc\new\EasyVocal.exe", @"C:\Temp\EasyVocal_update\abc", @"C:\Data\update.log",
            true, "--data \"C:\\Área de Trabalho\\d\"");
        var script = HelperScript.Build(plan);
        Assert.Contains("$WaitPid = 1234\r\n", script);
        Assert.Contains(@"$Target = 'C:\Users\Zé\OneDrive\Área de Trabalho\it''s mine\EasyVocal.exe'", script);
        Assert.Contains(@"$NewExe = 'C:\Temp\EasyVocal_update\abc\new\EasyVocal.exe'", script);
        Assert.Contains("$Relaunch = $true", script);
        Assert.Contains("$RelaunchArgs = '--data \"C:\\Área de Trabalho\\d\"'", script);
        Assert.Contains("$Backup = $Target + '.old'", script);
        Assert.Contains("WaitForExit(60000)", script);
        // Everything but the path lines is plain ASCII (Windows PowerShell must read it whatever the code page).
        var body = string.Join("\n", script.Split('\n').Where(l => !l.StartsWith('$') || l.StartsWith("$ErrorActionPreference") || l.StartsWith("$Backup") || l.StartsWith("$moved")));
        Assert.All(body, c => Assert.True(c < 128, "non-ASCII in the script body: " + c));
        Assert.Contains("$Relaunch = $false", HelperScript.Build(plan with { Relaunch = false }));
        Assert.True(HelperScript.FileEncoding.GetPreamble().SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    /// <summary>A pretend install: <c>…\Área de Trabalho\it's “mine”\EasyVocal.exe</c> plus a staging folder with the new exe.</summary>
    private (string Target, string NewExe, string Staging, string Log) PretendInstall()
    {
        var installDir = Path.Combine(temp, "Área de Trabalho", "it's ‘mine’");
        Directory.CreateDirectory(installDir);
        var target = Path.Combine(installDir, "EasyVocal.exe");
        File.WriteAllText(target, "MZ old version");
        var staging = Path.Combine(temp, "EasyVocal_update", "ç attempt");
        Directory.CreateDirectory(Path.Combine(staging, "new"));
        var newExe = Path.Combine(staging, "new", "EasyVocal.exe");
        File.WriteAllText(newExe, "MZ new version");
        return (target, newExe, staging, Path.Combine(temp, "dáta", "update.log"));
    }

    private static int RunScript(string scriptPath)
    {
        using var p = Process.Start(HelperScript.StartInfo(scriptPath))!;
        Assert.True(p.WaitForExit(60_000), "the helper script did not finish");
        return p.ExitCode;
    }

    private string WriteScript(HelperPlan plan)
    {
        var path = Path.Combine(temp, "scripts ü", "update.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, HelperScript.Build(plan), HelperScript.FileEncoding);
        return path;
    }

    [Fact]
    public void Helper_ReplacesTheExe_InAFolderWithAccentsSpacesAndQuotes()
    {
        var (target, newExe, staging, log) = PretendInstall();
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var script = WriteScript(new HelperPlan(-1, target, newExe, staging, log, Relaunch: false, ""));

        Assert.Equal(0, RunScript(script));

        Assert.Equal("MZ new version", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".old"), "the backup is deleted once the new exe is in place");
        Assert.False(Directory.Exists(staging), "the download folder is removed");
        Assert.Contains("updated", File.ReadAllText(log));
    }

    [Fact]
    public void Helper_PutsTheOldExeBack_WhenTheCopyFails()
    {
        var (target, newExe, staging, log) = PretendInstall();
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.Delete(newExe); // nothing to copy: Copy-Item fails after the old exe was moved aside

        Assert.Equal(3, RunScript(WriteScript(new HelperPlan(-1, target, newExe, staging, log, Relaunch: false, ""))));

        Assert.Equal("MZ old version", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".old"));
        Assert.Contains("put back", File.ReadAllText(log));
    }

    [Fact]
    public void Helper_WaitsForEasyVocalToClose()
    {
        var (target, newExe, staging, log) = PretendInstall();
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        // a process that lives ~2 s stands in for EasyVocal
        using var app = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 3 127.0.0.1")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        var sw = Stopwatch.StartNew();
        Assert.Equal(0, RunScript(WriteScript(new HelperPlan(app.Id, target, newExe, staging, log, Relaunch: false, ""))));
        Assert.True(app.HasExited, "the swap happened only after the program closed");
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(1));
        Assert.Equal("MZ new version", File.ReadAllText(target));
    }

    // ---------- the service, with fakes ----------

    private sealed class FakeSource : IUpdateSource
    {
        public string? Json;
        public Exception? CheckError;
        public string? ZipPath;
        public int Downloads;
        public TaskCompletionSource? Gate;

        public Task<string> GetLatestReleaseJsonAsync(CancellationToken ct) =>
            CheckError != null ? Task.FromException<string>(CheckError) : Task.FromResult(Json ?? "");

        public async Task DownloadAsync(string url, string path, long maxBytes, IProgress<double>? progress, CancellationToken ct)
        {
            Downloads++;
            if (Gate != null) await Gate.Task.WaitAsync(ct);
            File.Copy(ZipPath ?? throw new HttpRequestException("offline"), path, overwrite: true);
            progress?.Report(1);
        }
    }

    private sealed class FakeLauncher : IHelperLauncher
    {
        public readonly List<string> Started = new();
        public void Start(string scriptPath) => Started.Add(scriptPath);
    }

    private (UpdateService Service, FakeSource Source, FakeLauncher Launcher, string Exe) MakeService()
    {
        var installDir = Path.Combine(temp, "Área de Trabalho", "EasyVocal");
        Directory.CreateDirectory(installDir);
        var exe = Path.Combine(installDir, "EasyVocal.exe");
        File.WriteAllText(exe, "MZ old");
        var source = new FakeSource();
        var launcher = new FakeLauncher();
        return (new UpdateService(Win, source, launcher, new AppVersion(0, 2, 0), exe, Path.Combine(temp, "work")), source, launcher, exe);
    }

    [Fact]
    public async Task Check_TellsUpToDateAvailableOrFailed()
    {
        var (service, source, _, _) = MakeService();
        source.Json = ReleaseJson("v0.2.0");
        Assert.Equal(UpdateStatus.UpToDate, (await service.CheckAsync(null, manual: false)).Status);

        source.Json = ReleaseJson("v0.3.0");
        var available = await service.CheckAsync(null, manual: false);
        Assert.Equal(UpdateStatus.Available, available.Status);
        Assert.Equal("EasyVocal-win-x64.zip", available.Asset!.Name);
        Assert.Equal(UpdateStatus.UpToDate, (await service.CheckAsync("0.3.0", manual: false)).Status);
        Assert.Equal(UpdateStatus.Available, (await service.CheckAsync("0.3.0", manual: true)).Status);

        source.Json = ReleaseJson("v0.3.0", assets: "[]");
        Assert.Equal(UpdateStatus.Failed, (await service.CheckAsync(null, manual: true)).Status);
        source.Json = "{\"message\":\"Not Found\"}";
        Assert.Equal(UpdateStatus.Failed, (await service.CheckAsync(null, manual: true)).Status);
        source.CheckError = new HttpRequestException("no internet");
        Assert.Equal(UpdateStatus.Failed, (await service.CheckAsync(null, manual: true)).Status);
        source.CheckError = new TaskCanceledException("timeout");
        Assert.Equal(UpdateStatus.Failed, (await service.CheckAsync(null, manual: true)).Status);
    }

    [Fact]
    public async Task Download_ThenInstall_WritesTheHelperAndStartsIt()
    {
        var (service, source, launcher, exe) = MakeService();
        source.ZipPath = MakeZip("release.zip", ("EasyVocal.exe", FakeExe));
        var asset = new ReleaseAsset("EasyVocal-win-x64.zip", "https://example.com/e.zip", 1000);
        double last = 0;
        var newExe = await service.DownloadAsync(asset, new SyncProgress(p => last = p), CancellationToken.None);
        Assert.Equal(FakeExe, File.ReadAllBytes(newExe));
        Assert.Equal(1, last);
        Assert.StartsWith(Path.Combine(temp, "work"), newExe);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(newExe)!)!, "release.zip")), "the zip is removed once unpacked");

        var log = Path.Combine(temp, "update.log");
        var script = service.StartInstall(newExe, 4321, log, "--data \"C:\\d\"");
        Assert.Equal(new[] { script }, launcher.Started);
        var bytes = File.ReadAllBytes(script);
        Assert.True(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "the script has a BOM");
        var text = File.ReadAllText(script);
        Assert.Contains("$Target = " + HelperScript.Quote(exe), text);
        Assert.Contains("$NewExe = " + HelperScript.Quote(newExe), text);
        Assert.Contains("$WaitPid = 4321", text);
        Assert.Equal("MZ old", File.ReadAllText(exe)); // nothing is replaced until the helper runs
    }

    [Fact]
    public async Task Download_FailuresLeaveNothingBehind()
    {
        var (service, source, _, _) = MakeService();
        var work = Path.Combine(temp, "work");
        var asset = new ReleaseAsset("EasyVocal-win-x64.zip", "https://example.com/e.zip", 1000);

        var tooBig = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(asset with { Size = UpdateRules.MaxDownloadBytes + 1 }, null, default));
        Assert.Equal(UpdateProblem.TooBig, tooBig.Problem);
        Assert.Equal(0, source.Downloads);

        var offline = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(asset, null, default));
        Assert.Equal(UpdateProblem.Download, offline.Problem);

        source.ZipPath = MakeZip("bad.zip", ("Readme.txt", new byte[] { 1 }));
        Assert.Equal(UpdateProblem.MissingExe, (await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(asset, null, default))).Problem);

        source.ZipPath = MakeZip("good.zip", ("EasyVocal.exe", FakeExe));
        source.Gate = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();
        var running = service.DownloadAsync(asset, null, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Empty(Directory.Exists(work) ? Directory.GetFileSystemEntries(work) : Array.Empty<string>());
    }

    [Fact]
    public void ExtractExe_Cancelled_StopsAndRemovesWhatItWrote()
    {
        var zip = MakeZip("big.zip", ("EasyVocal.exe", FakeExe));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => UpdatePackage.ExtractExe(zip, Path.Combine(temp, "cancelled"), Win, cancel.Token));
        Assert.False(File.Exists(Path.Combine(temp, "cancelled", "EasyVocal.exe")));
    }

    [Fact]
    public async Task Download_CancelledWhileTheZipIsChecked_LeavesNothingBehind()
    {
        // "Checking the download…" (the progress reached 100 %) and the window closes.
        var (service, source, _, _) = MakeService();
        source.ZipPath = MakeZip("release.zip", ("EasyVocal.exe", FakeExe));
        var asset = new ReleaseAsset("EasyVocal-win-x64.zip", "https://example.com/e.zip", 1000);
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p >= 1) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(asset, progress, cancel.Token));
        var work = Path.Combine(temp, "work");
        Assert.Empty(Directory.Exists(work) ? Directory.GetFileSystemEntries(work) : Array.Empty<string>());
    }

    [Fact]
    public async Task Discard_RemovesADownloadNobodyInstalls()
    {
        var (service, source, _, exe) = MakeService();
        source.ZipPath = MakeZip("release.zip", ("EasyVocal.exe", FakeExe));
        var asset = new ReleaseAsset("EasyVocal-win-x64.zip", "https://example.com/e.zip", 1000);
        var newExe = await service.DownloadAsync(asset, null, CancellationToken.None);
        service.Discard(newExe);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(temp, "work")));
        service.Discard(Path.Combine(Path.GetDirectoryName(exe)!, "new", "EasyVocal.exe")); // not a download: left alone
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void CleanStale_RemovesTheOldCopy()
    {
        var (service, _, _, exe) = MakeService();
        File.WriteAllText(exe + ".old", "left by an update");
        service.CleanStale();
        Assert.False(File.Exists(exe + ".old"));
        Assert.True(File.Exists(exe));
        Assert.True(service.CanReplaceExe());
    }

    // ---------- helpers ----------

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
