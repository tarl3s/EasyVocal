using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using EasyVocal;

namespace EasyVocal.Tests;

/// <summary>The updater without the network: versions, GitHub's answer, the zip, the helper script (run for real on
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

    private static readonly byte[] FakeExe = Encoding.ASCII.GetBytes("MZ this stands in for EasyVocal.exe");

    // ---------- versions ----------

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData(" V0.10.10 ", 0, 10, 10, null)]
    [InlineData("v1.2", 1, 2, 0, null)]
    [InlineData("2", 2, 0, 0, null)]
    [InlineData("0.2.0+4f3a2b1c", 0, 2, 0, null)]
    [InlineData("v1.0.0-beta.1", 1, 0, 0, "beta.1")]
    [InlineData("1.0.0-rc-2+build.5", 1, 0, 0, "rc-2")]
    public void Version_Parses(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(AppVersion.TryParse(text, out var v));
        Assert.Equal(new AppVersion(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("1.2.3.4")]
    [InlineData("1..2")]
    [InlineData("1.x")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-be ta")]
    [InlineData("v 1.2.3")]
    [InlineData("99999999999.0.0")]
    [InlineData("１.２.３")]
    public void Version_RefusesGarbage(string? text)
    {
        Assert.False(AppVersion.TryParse(text, out _));
        Assert.Null(AppVersion.Parse(text));
    }

    [Fact]
    public void Version_ComparesLikeSemver()
    {
        string[] ascending =
        {
            "0.9", "0.10.9", "v0.10.10", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1", "2.0.0",
        };
        for (int i = 1; i < ascending.Length; i++)
        {
            var lo = AppVersion.Parse(ascending[i - 1])!.Value;
            var hi = AppVersion.Parse(ascending[i])!.Value;
            Assert.True(hi > lo, ascending[i] + " > " + ascending[i - 1]);
            Assert.True(lo < hi && lo.CompareTo(hi) < 0 && hi.CompareTo(lo) > 0);
        }
        Assert.Equal(AppVersion.Parse("v1.2"), AppVersion.Parse("1.2.0"));
        Assert.True(AppVersion.Parse("1.2")!.Value >= AppVersion.Parse("1.2.0+x")!.Value);
        Assert.Equal("1.0.0-beta.1", AppVersion.Parse("v1.0.0-beta.1+abc").ToString());
    }

    [Fact]
    public void RunningVersion_IsTheVersionInDirectoryBuildProps()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var expected = AppVersion.Parse(Regex.Match(props, @"<Version>([^<]+)</Version>").Groups[1].Value);
        Assert.Equal(expected, UpdateRules.RunningVersion(typeof(UpdaterTests).Assembly)); // informational "0.2.0+commit" included
    }

    // ---------- GitHub's answer ----------

    private static string ReleaseJson(string tag = "v0.3.0", string extra = "", string? assets = null) =>
        "{ \"tag_name\": \"" + tag + "\", \"name\": \"EasyVocal " + tag + "\", \"body\": \"## New\\n- Updates itself\"," +
        " \"html_url\": \"https://github.com/tarl3s/EasyVocal/releases/tag/" + tag + "\"," + extra +
        " \"assets\": " + (assets ?? "[{ \"name\": \"EasyVocal-win-x64.zip\", \"size\": 70000000, \"browser_download_url\": \"https://github.com/tarl3s/EasyVocal/releases/download/" + tag + "/EasyVocal-win-x64.zip\" }]") + " }";

    [Fact]
    public void ParseRelease_ReadsGitHubsAnswer()
    {
        var r = UpdateRules.ParseRelease(ReleaseJson())!;
        Assert.Equal(new AppVersion(0, 3, 0), r.Version);
        Assert.Equal("v0.3.0", r.Tag);
        Assert.Equal("## New\n- Updates itself", r.Notes);
        Assert.False(r.Prerelease);
        Assert.False(r.Draft);
        Assert.Equal("https://github.com/tarl3s/EasyVocal/releases/tag/v0.3.0", r.PageUrl);
        var a = Assert.Single(r.Assets);
        Assert.Equal("EasyVocal-win-x64.zip", a.Name);
        Assert.Equal(70000000, a.Size);
    }

    [Fact]
    public void ParseRelease_IsTolerant()
    {
        // comments, trailing commas, a size in quotes, no body, a page that isn't https, null and odd assets
        var json = """
            {
              // GitHub never sends comments, but a proxy might mangle things
              "tag_name": "0.4",
              "body": null,
              "html_url": "javascript:alert(1)",
              "prerelease": "false",
              "assets": [
                null, 42, { "name": "no url" },
                { "name": "EasyVocal-win-x64.zip", "size": "123", "browser_download_url": "https://example.com/e.zip", },
                { "name": "plain-http.zip", "browser_download_url": "http://example.com/e.zip" },
              ],
            }
            """;
        var r = UpdateRules.ParseRelease(json)!;
        Assert.Equal(new AppVersion(0, 4, 0), r.Version);
        Assert.Equal("", r.Notes);
        Assert.Equal(UpdateRules.PageUrl, r.PageUrl);
        Assert.False(r.Prerelease);
        var a = Assert.Single(r.Assets);
        Assert.Equal(123, a.Size);

        var noAssets = UpdateRules.ParseRelease("{\"tag_name\":\"v1.0.0\",\"assets\":\"none\"}")!;
        Assert.Empty(noAssets.Assets);
        Assert.Equal(-1, UpdateRules.ParseRelease("{\"tag_name\":\"v1.0.0\",\"assets\":[{\"name\":\"x.zip\",\"browser_download_url\":\"https://e.com/x\"}]}")!.Assets[0].Size);
        Assert.True(UpdateRules.ParseRelease(ReleaseJson(extra: " \"prerelease\": true, \"draft\": true,"))! is { Prerelease: true, Draft: true });

        var huge = UpdateRules.ParseRelease("{\"tag_name\":\"v1.0.0\",\"body\":\"" + new string('a', 20000) + "\"}")!;
        Assert.True(huge.Notes.Length <= UpdateRules.MaxNotesChars + 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"v1.0.0\"")]
    [InlineData("{\"message\": \"API rate limit exceeded\"}")]
    [InlineData("{\"tag_name\": \"nightly\", \"assets\": []}")]
    [InlineData("{\"tag_name\": 3}")]
    [InlineData("{\"tag_name\": \"v1.0.0\"")]
    public void ParseRelease_RefusesWhatIsNotARelease(string? json) => Assert.Null(UpdateRules.ParseRelease(json));

    // ---------- which file ----------

    private static ReleaseInfo WithAssets(params string[] names) => new(new AppVersion(0, 3, 0), "v0.3.0", "",
        names.Select(n => new ReleaseAsset(n, "https://example.com/" + n, 10)).ToList(), false, false, UpdateRules.PageUrl);

    [Fact]
    public void PickAsset_TakesThisPlatformsZip()
    {
        var r = WithAssets("EasyVocal-win-x64.zip.sha256", "Source code.zip", "easyvocal-WIN-x64.ZIP", "EasyVocal-win-arm64.zip");
        Assert.Equal("easyvocal-WIN-x64.ZIP", UpdateRules.PickAsset(r, Architecture.X64)!.Name);
        Assert.Equal("EasyVocal-win-arm64.zip", UpdateRules.PickAsset(r, Architecture.Arm64)!.Name);
        Assert.Equal("EasyVocal-win-x64.zip", UpdateRules.PickAsset(WithAssets("EasyVocal-win-x64.zip"), Architecture.Arm64)!.Name); // x64 runs on ARM
        Assert.Null(UpdateRules.PickAsset(r, Architecture.X86));
        Assert.Null(UpdateRules.PickAsset(WithAssets("EasyVocal-win-x64.zip.sha256", "EasyVocal.exe"), Architecture.X64));
    }

    // ---------- when to ask ----------

    private static ReleaseInfo Release(string tag, bool prerelease = false, bool draft = false) =>
        new(AppVersion.Parse(tag)!.Value, tag, "", Array.Empty<ReleaseAsset>(), prerelease, draft, UpdateRules.PageUrl);

    [Fact]
    public void ShouldOffer_OnlyANewerFinishedRelease()
    {
        var current = new AppVersion(0, 2, 0);
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.0"), current, null, manual: false));
        Assert.True(UpdateRules.ShouldOffer(Release("v0.2.1"), current, null, manual: false));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.2.0"), current, null, manual: true));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.1.2"), current, null, manual: true));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.3.0", prerelease: true), current, null, manual: true));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.3.0", draft: true), current, null, manual: true));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.3.0-beta.1"), current, null, manual: true));
        // a pre-release build is offered its final version
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.0"), new AppVersion(0, 3, 0, "beta.1"), null, manual: false));
    }

    [Fact]
    public void ShouldOffer_SkippedVersionIsOnlyOfferedWhenAsked()
    {
        var current = new AppVersion(0, 2, 0);
        Assert.False(UpdateRules.ShouldOffer(Release("v0.3.0"), current, "0.3.0", manual: false));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.3.0"), current, "v0.3", manual: false));
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.0"), current, "0.3.0", manual: true));   // "Check now" shows it anyway
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.1"), current, "0.3.0", manual: false));  // a newer one comes back
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.0"), current, "garbage", manual: false));
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.0"), current, "", manual: false));
    }

    // ---------- source builds ----------

    [Fact]
    public void IsSourceBuild_LooksForTheSolutionAbove()
    {
        var withSln = Path.Combine(temp, "repo");
        var bin = Path.Combine(withSln, "src", "EasyVocal", "bin", "Debug", "net8.0-windows");
        Directory.CreateDirectory(bin);
        Assert.False(UpdateRules.IsSourceBuild(bin));
        File.WriteAllText(Path.Combine(withSln, "EasyVocal.sln"), "");
        Assert.True(UpdateRules.IsSourceBuild(bin));
        Assert.True(UpdateRules.IsSourceBuild(Path.Combine(withSln, "publish", "win-x64")));

        var withProject = Path.Combine(temp, "checkout");
        Directory.CreateDirectory(Path.Combine(withProject, "src", "EasyVocal"));
        File.WriteAllText(Path.Combine(withProject, "src", "EasyVocal", "EasyVocal.csproj"), "");
        Assert.True(UpdateRules.IsSourceBuild(Path.Combine(withProject, "publish")));

        var downloaded = Path.Combine(temp, "Downloads", "EasyVocal");
        Directory.CreateDirectory(downloaded);
        Assert.False(UpdateRules.IsSourceBuild(downloaded));
        Assert.False(UpdateRules.IsSourceBuild(Path.Combine(temp, "does not exist")));
        Assert.True(UpdateRules.IsSourceBuild(AppContext.BaseDirectory)); // these tests run inside the repo
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
        var exe = UpdatePackage.ExtractExe(zip, Path.Combine(temp, "out"));
        Assert.Equal(Path.Combine(temp, "out", "EasyVocal.exe"), exe);
        Assert.Equal(FakeExe, File.ReadAllBytes(exe));
        Assert.False(File.Exists(Path.Combine(temp, "out", "README.txt"))); // only the exe is unpacked

        var nested = MakeZip("nested.zip", ("EasyVocal-0.3.0/EasyVocal.exe", FakeExe));
        Assert.Equal(FakeExe, File.ReadAllBytes(UpdatePackage.ExtractExe(nested, Path.Combine(temp, "out2"))));
    }

    [Fact]
    public void ExtractExe_RefusesBadZips()
    {
        void Fails(UpdateProblem problem, string zip) =>
            Assert.Equal(problem, Assert.Throws<UpdateException>(() => UpdatePackage.ExtractExe(zip, Path.Combine(temp, "x"))).Problem);

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
        return (new UpdateService(source, launcher, new AppVersion(0, 2, 0), exe, Path.Combine(temp, "work")), source, launcher, exe);
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
    public void CleanStale_RemovesTheOldCopy()
    {
        var (service, _, _, exe) = MakeService();
        File.WriteAllText(exe + ".old", "left by an update");
        service.CleanStale();
        Assert.False(File.Exists(exe + ".old"));
        Assert.True(File.Exists(exe));
        Assert.True(service.CanReplaceExe());
    }

    // ---------- release notes ----------

    [Fact]
    public void Notes_BecomePlainLines()
    {
        var lines = ReleaseNotes.ToLines("## What's new\r\n\r\n\r\n- **Updates** itself — see [the page](https://x.y)\n* `code` &amp; <b>tags</b>\n---\nPlain text ![img](a.png)\n\n");
        Assert.Equal(new[]
        {
            new NoteLine(NoteKind.Heading, "What's new"),
            new NoteLine(NoteKind.Blank, ""),
            new NoteLine(NoteKind.Bullet, "Updates itself — see the page"),
            new NoteLine(NoteKind.Bullet, "code & tags"),
            new NoteLine(NoteKind.Blank, ""),
            new NoteLine(NoteKind.Text, "Plain text"),
        }, lines);
        Assert.Empty(ReleaseNotes.ToLines(null));
        Assert.Empty(ReleaseNotes.ToLines("\n\n  \n"));
        Assert.True(ReleaseNotes.ToLines(string.Join("\n", Enumerable.Repeat("- x", 1000))).Count <= 200);
    }

    // ---------- helpers ----------

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Directory.Build.props not found above the test folder");
    }
}
