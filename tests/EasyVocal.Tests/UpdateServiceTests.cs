using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using EasyVocal.Core.Update;
using EasyVocal.Tests.Helpers;
using Xunit.Abstractions;

namespace EasyVocal.Tests;

/// <summary>
/// The updater on any system, with a pretend platform (<see cref="FakeUpdaterPlatform"/>) and no network: unpacking
/// (zip and tar.gz, zip-slip, links, size, permissions), the service's check / download / install / discard / clean.
/// </summary>
public class UpdateServiceTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string temp = Path.Combine(Path.GetTempPath(), "EV_Test_UpdateService_" + Guid.NewGuid().ToString("N"));

    public UpdateServiceTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(temp);
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(temp); attempt++)
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }

    private static readonly byte[] Prog = FakeUpdaterPlatform.Program;

    private string MakeZip(string name, params (string Entry, byte[] Data)[] files)
    {
        var path = Path.Combine(temp, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in files)
        {
            var e = zip.CreateEntry(entry);
            if (entry.EndsWith(".sh") || entry.EndsWith("EasyVocal-test")) e.ExternalAttributes = 0x81ED << 16; // -rwxr-xr-x
            using var s = e.Open();
            s.Write(data);
        }
        return path;
    }

    private string MakeTarGz(string name, Action<TarWriter> fill)
    {
        var path = Path.Combine(temp, name);
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            fill(tar);
        return path;
    }

    private static void AddFile(TarWriter tar, string name, byte[] data, UnixFileMode mode)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data), Mode = mode };
        tar.WriteEntry(entry);
    }

    private const UnixFileMode Exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode Plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    // ---------- unpacking ----------

    [Fact]
    public void ExtractFile_TakesOneFileOut_AtTheTopOrInOneFolder()
    {
        var zip = MakeZip("ok.zip", ("EasyVocal-test", Prog), ("README.txt", new byte[] { 1 }));
        var exe = UpdatePackage.ExtractFile(zip, Path.Combine(temp, "out"), "EasyVocal-test");
        Assert.Equal(Path.Combine(temp, "out", "EasyVocal-test"), exe);
        Assert.Equal(Prog, File.ReadAllBytes(exe));
        Assert.False(File.Exists(Path.Combine(temp, "out", "README.txt"))); // only that file is unpacked

        var nested = MakeZip("nested.zip", ("EasyVocal-1.0.0/EasyVocal-test", Prog));
        Assert.Equal(Prog, File.ReadAllBytes(UpdatePackage.ExtractFile(nested, Path.Combine(temp, "out2"), "EasyVocal-test")));
    }

    [Fact]
    public void ExtractFile_RefusesBadZips()
    {
        void Fails(UpdateProblem problem, string zip) =>
            Assert.Equal(problem, Assert.Throws<UpdateException>(() => UpdatePackage.ExtractFile(zip, Path.Combine(temp, "x"), "EasyVocal-test")).Problem);

        Fails(UpdateProblem.MissingExe, MakeZip("none.zip", ("Other", Prog)));
        Fails(UpdateProblem.MissingExe, MakeZip("deep.zip", ("a/b/EasyVocal-test", Prog)));
        Fails(UpdateProblem.MissingExe, MakeZip("empty.zip"));
        Fails(UpdateProblem.UnsafeZip, MakeZip("slip.zip", ("EasyVocal-test", Prog), ("../../evil.txt", new byte[] { 1 })));
        Fails(UpdateProblem.UnsafeZip, MakeZip("slip2.zip", ("..\\EasyVocal-test", Prog))); // a backslash is a folder part on every system
        var notZip = Path.Combine(temp, "page.zip");
        File.WriteAllText(notZip, "<html>rate limited</html>");
        Fails(UpdateProblem.NotAZip, notZip);
        Assert.False(File.Exists(Path.Combine(temp, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(temp)!, "evil.txt")));
    }

    [Fact]
    public void ExtractFile_Cancelled_StopsAndRemovesWhatItWrote()
    {
        var zip = MakeZip("big.zip", ("EasyVocal-test", Prog));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => UpdatePackage.ExtractFile(zip, Path.Combine(temp, "cancelled"), "EasyVocal-test", cancel.Token));
        Assert.False(File.Exists(Path.Combine(temp, "cancelled", "EasyVocal-test")));
    }

    [Fact]
    public void ExtractAll_Zip_KeepsFoldersAndTheExecutableBit()
    {
        var zip = MakeZip("all.zip", ("EasyVocal/EasyVocal-test", Prog), ("EasyVocal/lib/x.dll", new byte[] { 2 }), ("EasyVocal/run.sh", new byte[] { 3 }));
        var dest = Path.Combine(temp, "all");
        UpdatePackage.ExtractAll(zip, dest);
        Assert.Equal(Prog, File.ReadAllBytes(Path.Combine(dest, "EasyVocal", "EasyVocal-test")));
        Assert.True(File.Exists(Path.Combine(dest, "EasyVocal", "lib", "x.dll")));
        if (OperatingSystem.IsWindows())
        {
            output.WriteLine("SKIPPED (Windows has no Unix permission bits): the executable bit check");
            return;
        }
        Assert.True(File.GetUnixFileMode(Path.Combine(dest, "EasyVocal", "run.sh")).HasFlag(UnixFileMode.UserExecute));
        Assert.False(File.GetUnixFileMode(Path.Combine(dest, "EasyVocal", "lib", "x.dll")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void ExtractAll_TarGz_KeepsFoldersAndTheExecutableBit()
    {
        var tgz = MakeTarGz("all.tar.gz", tar =>
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "EasyVocal/"));
            AddFile(tar, "EasyVocal/EasyVocal-test", Prog, Exec);
            AddFile(tar, "EasyVocal/notes.txt", new byte[] { 1, 2 }, Plain);
        });
        var dest = Path.Combine(temp, "tgz");
        UpdatePackage.ExtractAll(tgz, dest);
        var exe = Path.Combine(dest, "EasyVocal", "EasyVocal-test");
        Assert.Equal(Prog, File.ReadAllBytes(exe));
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(dest, "EasyVocal", "notes.txt")));
        if (OperatingSystem.IsWindows())
        {
            output.WriteLine("SKIPPED (Windows has no Unix permission bits): the executable bit check");
            return;
        }
        Assert.True(File.GetUnixFileMode(exe).HasFlag(UnixFileMode.UserExecute));
        Assert.False(File.GetUnixFileMode(Path.Combine(dest, "EasyVocal", "notes.txt")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void ExtractAll_RefusesEscapesLinksAndGarbage()
    {
        void Fails(UpdateProblem problem, string archive) =>
            Assert.Equal(problem, Assert.Throws<UpdateException>(() => UpdatePackage.ExtractAll(archive, Path.Combine(temp, "bad"))).Problem);

        Fails(UpdateProblem.UnsafeZip, MakeTarGz("slip.tar.gz", tar => AddFile(tar, "../evil.txt", new byte[] { 1 }, Plain)));
        Fails(UpdateProblem.UnsafeZip, MakeTarGz("abs.tar.gz", tar => AddFile(tar, "/etc/evil.txt", new byte[] { 1 }, Plain)));
        Fails(UpdateProblem.UnsafeZip, MakeTarGz("link.tar.gz", tar =>
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "EasyVocal/link") { LinkName = "/etc/passwd" })));
        Fails(UpdateProblem.UnsafeZip, MakeZip("slip.zip", ("../evil.txt", new byte[] { 1 })));
        var junk = Path.Combine(temp, "junk.tar.gz");
        File.WriteAllBytes(junk, new byte[] { 0x1F, 0x8B, 8, 0, 1, 2, 3, 4, 5, 6 });
        Fails(UpdateProblem.NotAZip, junk);
        Assert.False(File.Exists(Path.Combine(temp, "evil.txt")));
    }

    [Fact]
    public void ExtractAll_AFileWhereAFolderShouldBe_IsABrokenDownload()
    {
        void Fails(string archive) =>
            Assert.Equal(UpdateProblem.UnsafeZip, Assert.Throws<UpdateException>(() => UpdatePackage.ExtractAll(archive, Path.Combine(temp, "nested-" + Path.GetFileName(archive)))).Problem);

        Fails(MakeZip("nested.zip", ("EasyVocal/a", new byte[] { 1 }), ("EasyVocal/a/b", new byte[] { 2 })));
        Fails(MakeTarGz("nested.tar.gz", tar =>
        {
            AddFile(tar, "EasyVocal/a", new byte[] { 1 }, Plain);
            AddFile(tar, "EasyVocal/a/b", new byte[] { 2 }, Plain);
        }));
    }

    [Fact]
    public void ExtractAll_KeepsOnlyTheReadWriteExecuteBits()
    {
        var tgz = MakeTarGz("suid.tar.gz", tar => AddFile(tar, "EasyVocal/EasyVocal-test", Prog, Exec | UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit));
        var path = Path.Combine(temp, "suid.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("EasyVocal/run.sh");
            e.ExternalAttributes = (0x8000 | 0xFED) << 16; // a regular file, rwxr-xr-x plus setuid, setgid and sticky
            using var s = e.Open();
            s.WriteByte(1);
        }
        var dest = Path.Combine(temp, "suid");
        UpdatePackage.ExtractAll(tgz, dest);
        UpdatePackage.ExtractAll(path, dest);
        if (OperatingSystem.IsWindows())
        {
            output.WriteLine("SKIPPED (Windows has no Unix permission bits): the setuid check");
            return;
        }
        foreach (var file in new[] { Path.Combine(dest, "EasyVocal", "EasyVocal-test"), Path.Combine(dest, "EasyVocal", "run.sh") })
        {
            var mode = File.GetUnixFileMode(file);
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute), file);
            Assert.False(mode.HasFlag(UnixFileMode.SetUser) || mode.HasFlag(UnixFileMode.SetGroup) || mode.HasFlag(UnixFileMode.StickyBit), file + ": " + mode);
        }
    }

    // ---------- the service ----------

    private static string ReleaseJson(string tag, string? assets = null) =>
        "{ \"tag_name\": \"" + tag + "\", \"body\": \"## New\", \"html_url\": \"https://github.com/tarl3s/EasyVocal/releases/tag/" + tag + "\"," +
        " \"assets\": " + (assets ?? "[{ \"name\": \"EasyVocal-test.zip\", \"size\": 1000, \"browser_download_url\": \"https://example.com/EasyVocal-test.zip\" }]") + " }";

    private (UpdateService Service, FakeUpdateSource Source, FakeUpdaterPlatform Platform, string Exe) MakeService(bool wholeArchive = false)
    {
        var installDir = Path.Combine(temp, "Área de Trabalho", "EasyVocal");
        Directory.CreateDirectory(installDir);
        var exe = Path.Combine(installDir, "EasyVocal-test");
        File.WriteAllText(exe, "PROG old");
        var source = new FakeUpdateSource();
        var platform = new FakeUpdaterPlatform { WholeArchive = wholeArchive };
        var service = new UpdateService(platform, source, platform.Launcher, new AppVersion(0, 2, 0), exe, Path.Combine(temp, "work"));
        return (service, source, platform, exe);
    }

    private string Work => Path.Combine(temp, "work");

    private string[] WorkEntries() => Directory.Exists(Work) ? Directory.GetFileSystemEntries(Work) : Array.Empty<string>();

    [Fact]
    public async Task Check_TellsUpToDateAvailableOrFailed()
    {
        var (service, source, _, _) = MakeService();
        source.Json = ReleaseJson("v0.2.0");
        Assert.Equal(UpdateStatus.UpToDate, (await service.CheckAsync(null, manual: false)).Status);

        source.Json = ReleaseJson("v0.3.0");
        var available = await service.CheckAsync(null, manual: false);
        Assert.Equal(UpdateStatus.Available, available.Status);
        Assert.Equal("EasyVocal-test.zip", available.Asset!.Name);
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
    public async Task Download_ThenInstall_WritesThePlatformsHelperAndStartsIt()
    {
        var (service, source, platform, exe) = MakeService();
        source.ArchivePath = MakeZip("release.zip", ("EasyVocal-test", Prog));
        var asset = new ReleaseAsset("EasyVocal-test.zip", "https://example.com/e.zip", 1000);
        double last = 0;
        var payload = await service.DownloadAsync(asset, new SyncProgress(p => last = p), CancellationToken.None);
        Assert.Equal(Prog, File.ReadAllBytes(payload));
        Assert.Equal(1, last);
        Assert.StartsWith(Work, payload);
        var staging = Path.GetDirectoryName(Path.GetDirectoryName(payload)!)!;
        Assert.False(File.Exists(Path.Combine(staging, "release.zip")), "the archive is removed once unpacked");

        var script = service.StartInstall(payload, 4321, Path.Combine(temp, "update.log"), "--data d");
        Assert.Equal(new[] { script }, platform.FakeLauncher.Started);
        Assert.Equal(Path.Combine(staging, "update.txt"), script);
        var text = File.ReadAllText(script);
        Assert.Contains("pid=4321", text);
        Assert.Contains("target=" + exe, text);
        Assert.Contains("new=" + payload, text);
        Assert.Contains("staging=" + staging, text);
        Assert.Contains("relaunch=True --data d", text);
        Assert.Equal("PROG old", File.ReadAllText(exe)); // nothing is replaced until the helper runs
    }

    [Fact]
    public async Task Download_ATarGzRelease_GivesTheWholeFolder_AndReplacesTheInstallRoot()
    {
        var (service, source, platform, exe) = MakeService(wholeArchive: true);
        source.ArchivePath = MakeTarGz("release.tar.gz", tar =>
        {
            AddFile(tar, "EasyVocal/EasyVocal-test", Prog, Exec);
            AddFile(tar, "EasyVocal/lib.dll", new byte[] { 9 }, Plain);
        });
        var asset = new ReleaseAsset("EasyVocal-test.tar.gz", "https://example.com/e.tar.gz", 1000);
        var payload = await service.DownloadAsync(asset, null, CancellationToken.None);
        Assert.True(Directory.Exists(payload));
        Assert.True(File.Exists(Path.Combine(payload, "lib.dll")));
        var script = service.StartInstall(payload, 1, Path.Combine(temp, "u.log"), "");
        Assert.Contains("target=" + Path.GetDirectoryName(exe), File.ReadAllText(script)); // the folder, not the program
        service.Discard(payload);
        Assert.Empty(WorkEntries());
    }

    [Fact]
    public async Task Download_FailuresLeaveNothingBehind()
    {
        var (service, source, _, _) = MakeService();
        var asset = new ReleaseAsset("EasyVocal-test.zip", "https://example.com/e.zip", 1000);

        var tooBig = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(asset with { Size = UpdateRules.MaxDownloadBytes + 1 }, null, default));
        Assert.Equal(UpdateProblem.TooBig, tooBig.Problem);
        Assert.Equal(0, source.Downloads);

        var offline = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(asset, null, default));
        Assert.Equal(UpdateProblem.Download, offline.Problem);

        source.ArchivePath = MakeZip("bad.zip", ("Readme.txt", new byte[] { 1 }));
        Assert.Equal(UpdateProblem.MissingExe, (await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(asset, null, default))).Problem);

        source.ArchivePath = MakeZip("text.zip", ("EasyVocal-test", Encoding.ASCII.GetBytes("<html>404</html>")));
        Assert.Equal(UpdateProblem.NotAnExe, (await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(asset, null, default))).Problem);

        source.ArchivePath = MakeZip("good.zip", ("EasyVocal-test", Prog));
        source.Gate = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();
        var running = service.DownloadAsync(asset, null, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Empty(WorkEntries());
    }

    [Fact]
    public async Task Download_CancelledWhileTheArchiveIsChecked_LeavesNothingBehind()
    {
        // "Checking the download…" (the progress reached 100 %) and the window closes.
        var (service, source, _, _) = MakeService();
        source.ArchivePath = MakeZip("release.zip", ("EasyVocal-test", Prog));
        var asset = new ReleaseAsset("EasyVocal-test.zip", "https://example.com/e.zip", 1000);
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p >= 1) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(asset, progress, cancel.Token));
        Assert.Empty(WorkEntries());
    }

    [Fact]
    public async Task Discard_RemovesADownloadNobodyInstalls()
    {
        var (service, source, _, exe) = MakeService();
        source.ArchivePath = MakeZip("release.zip", ("EasyVocal-test", Prog));
        var asset = new ReleaseAsset("EasyVocal-test.zip", "https://example.com/e.zip", 1000);
        var payload = await service.DownloadAsync(asset, null, CancellationToken.None);
        service.Discard(payload);
        Assert.Empty(WorkEntries());
        service.Discard(Path.Combine(Path.GetDirectoryName(exe)!, "new", "EasyVocal-test")); // not a download: left alone
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public async Task StartInstall_WhenThePlatformCantSelfUpdate_StartsNothing()
    {
        var (service, source, platform, _) = MakeService();
        source.ArchivePath = MakeZip("release.zip", ("EasyVocal-test", Prog));
        var payload = await service.DownloadAsync(new ReleaseAsset("EasyVocal-test.zip", "https://example.com/e.zip", 1000), null, default);
        platform.Writable = false;
        Assert.False(service.CanSelfUpdate());
        var ex = Assert.Throws<UpdateException>(() => service.StartInstall(payload, 1, Path.Combine(temp, "u.log"), ""));
        Assert.Equal(UpdateProblem.FolderNotWritable, ex.Problem);
        Assert.Contains("EasyVocal-test", ex.Message); // the platform's program name, not a hard-coded one
        Assert.Empty(platform.FakeLauncher.Started);
    }

    [Fact]
    public void CleanStale_RemovesTheOldCopy()
    {
        var (service, _, _, exe) = MakeService();
        File.WriteAllText(exe + ".old", "left by an update");
        service.CleanStale();
        Assert.False(File.Exists(exe + ".old"));
        Assert.True(File.Exists(exe));
        Assert.True(service.CanSelfUpdate());
    }

    [Fact]
    public void CleanStale_RemovesAnOldFolder_ForPlatformsThatReplaceAFolder()
    {
        var (service, _, _, exe) = MakeService(wholeArchive: true); // the install root is the app's folder (macOS .app, Linux)
        var old = Path.GetDirectoryName(exe) + ".old";
        Directory.CreateDirectory(Path.Combine(old, "lib"));
        File.WriteAllText(Path.Combine(old, "lib", "x.dll"), "left by an update");
        service.CleanStale();
        Assert.False(Directory.Exists(old));
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public async Task StartInstall_APayloadOutsideTheUpdatersFolder_IsRefused()
    {
        var (service, source, platform, exe) = MakeService();
        // The helper deletes its staging folder at the end: for a payload elsewhere that could be any folder above it.
        foreach (var payload in new[] { exe, Path.Combine(Work, "EasyVocal-test"), Path.Combine(temp, "workX", "a", "EasyVocal-test") })
        {
            var ex = Assert.Throws<UpdateException>(() => service.StartInstall(payload, 1, Path.Combine(temp, "u.log"), ""));
            Assert.Equal(UpdateProblem.Launch, ex.Problem);
        }
        Assert.Empty(platform.FakeLauncher.Started);
        Assert.True(File.Exists(exe));

        source.ArchivePath = MakeZip("release.zip", ("EasyVocal-test", Prog));
        var real = await service.DownloadAsync(new ReleaseAsset("EasyVocal-test.zip", "https://example.com/e.zip", 1000), null, default);
        var script = service.StartInstall(real, 1, Path.Combine(temp, "u.log"), "");
        Assert.Equal(Path.GetFullPath(Work), Path.GetDirectoryName(Path.GetDirectoryName(script)));
        Assert.Contains("staging=" + Path.GetDirectoryName(script), File.ReadAllText(script));
    }
}
