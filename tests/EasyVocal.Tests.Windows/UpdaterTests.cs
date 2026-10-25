using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using EasyVocal.Core.Update;
using EasyVocal.Platform.Windows;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

/// <summary>The updater on Windows, without the network: the release zip, the PowerShell helper (run for real on
/// throwaway folders whose names have spaces, accents and quotes) and the service with Windows' platform.</summary>
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
    public void Unpack_TakesTheExeOut_AndRefusesANonProgram()
    {
        var zip = MakeZip("ok.zip", ("EasyVocal.exe", FakeExe), ("README.txt", new byte[] { 1 }));
        var exe = Win.Unpack(zip, Path.Combine(temp, "out"), default);
        Assert.Equal(Path.Combine(temp, "out", "EasyVocal.exe"), exe);
        Assert.Equal(FakeExe, File.ReadAllBytes(exe));
        Assert.False(File.Exists(Path.Combine(temp, "out", "README.txt"))); // only the exe is unpacked

        var text = MakeZip("text.zip", ("EasyVocal.exe", Encoding.ASCII.GetBytes("<html>404</html>")));
        Assert.Equal(UpdateProblem.NotAnExe, Assert.Throws<UpdateException>(() => Win.Unpack(text, Path.Combine(temp, "x"), default)).Problem);
        Assert.False(File.Exists(Path.Combine(temp, "x", "EasyVocal.exe"))); // the non-program was removed again
        Assert.Equal(UpdateProblem.MissingExe,
            Assert.Throws<UpdateException>(() => Win.Unpack(MakeZip("none.zip", ("Other.exe", FakeExe)), Path.Combine(temp, "y"), default)).Problem);
    }

    [Fact]
    public void InstallRootAndSelfUpdate_AreTheExeAndItsFolder()
    {
        var dir = Path.Combine(temp, "app");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "EasyVocal.exe");
        Assert.Equal(exe, Win.InstallRoot(exe));
        Assert.True(Win.CanSelfUpdate(exe));
        Assert.Empty(Directory.GetFiles(dir)); // the probe file is gone again
        Assert.False(Win.CanSelfUpdate(Path.Combine(temp, "missing", "EasyVocal.exe")));
        Assert.IsType<PowerShellLauncher>(Win.Launcher);
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


    // ---------- the service with Windows' platform (the rest is in the portable UpdateServiceTests) ----------

    [Fact]
    public async Task Download_ThenInstall_WritesThePowerShellHelper()
    {
        var installDir = Path.Combine(temp, "Área de Trabalho", "EasyVocal");
        Directory.CreateDirectory(installDir);
        var exe = Path.Combine(installDir, "EasyVocal.exe");
        File.WriteAllText(exe, "MZ old");
        var source = new FakeUpdateSource { ArchivePath = MakeZip("release.zip", ("EasyVocal.exe", FakeExe)) };
        var launcher = new FakeHelperLauncher();
        var service = new UpdateService(Win, source, launcher, new AppVersion(0, 2, 0), exe, Path.Combine(temp, "work"));
        var newExe = await service.DownloadAsync(new ReleaseAsset("EasyVocal-win-x64.zip", "https://example.com/e.zip", 1000), null, CancellationToken.None);
        Assert.Equal(FakeExe, File.ReadAllBytes(newExe));

        var script = service.StartInstall(newExe, 4321, Path.Combine(temp, "update.log"), "--data \"C:\\d\"");
        Assert.Equal(new[] { script }, launcher.Started);
        Assert.Equal("update.ps1", Path.GetFileName(script));
        var bytes = File.ReadAllBytes(script);
        Assert.True(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "the script has a BOM");
        var text = File.ReadAllText(script);
        Assert.Contains("$Target = " + HelperScript.Quote(exe), text);
        Assert.Contains("$NewExe = " + HelperScript.Quote(newExe), text);
        Assert.Contains("$WaitPid = 4321", text);
        Assert.Equal("MZ old", File.ReadAllText(exe)); // nothing is replaced until the helper runs
    }
}
