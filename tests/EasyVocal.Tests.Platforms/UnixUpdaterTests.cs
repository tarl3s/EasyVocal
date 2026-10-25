using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using EasyVocal.Core.Update;
using EasyVocal.Platform.Unix;
using Xunit.Abstractions;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests.Platforms;

/// <summary>The macOS/Linux updater: release names, what is replaced, unpacking, and the sh helper (run for real on macOS/Linux).</summary>
public class UnixUpdaterTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string temp = TestFolders.New("EV_Test_UnixUpdate_");
    private static readonly byte[] MachO = { 0xCF, 0xFA, 0xED, 0xFE, 7, 0, 0, 1 };
    private static readonly byte[] Universal = { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2 };
    private static readonly byte[] Elf = { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0 };
    /// <summary>The release's easyvocal-files.txt (what the packager writes into every install root).</summary>
    private static readonly byte[] Marker = System.Text.Encoding.UTF8.GetBytes("EasyVocal\neasyvocal-files.txt\n");

    public UnixUpdaterTests(ITestOutputHelper output) => this.output = output;

    public void Dispose() => TestFolders.Delete(temp);

    private static UnixUpdaterPlatform Mac => UnixUpdaterPlatform.MacOS;
    private static UnixUpdaterPlatform Linux => UnixUpdaterPlatform.Linux;

    [Fact]
    public void AssetNames_PerSystemAndChip()
    {
        Assert.Equal(new[] { "EasyVocal-macos-arm64.zip", "EasyVocal-macos-x64.zip" }, Mac.AssetNames(Architecture.Arm64));
        Assert.Equal(new[] { "EasyVocal-macos-x64.zip" }, Mac.AssetNames(Architecture.X64));
        Assert.Equal(new[] { "EasyVocal-linux-x64.tar.gz" }, Linux.AssetNames(Architecture.X64));
        Assert.Empty(Linux.AssetNames(Architecture.Arm64));
        Assert.Equal("EasyVocal", Mac.ExeName);
        Assert.Equal("update.sh", Linux.HelperFileName);
        Assert.Empty(Linux.HelperEncoding.GetPreamble()); // sh doesn't want a BOM
    }

    [Fact]
    public void InstallRoot_IsTheAppBundleOrTheFolder()
    {
        var app = Path.Combine(temp, "Applications", "EasyVocal.app");
        Assert.Equal(app, Mac.InstallRoot(Path.Combine(app, "Contents", "MacOS", "EasyVocal")));
        var folder = Path.Combine(temp, "opt", "EasyVocal");
        Assert.Equal(folder, Linux.InstallRoot(Path.Combine(folder, "EasyVocal")));
        Assert.Equal(folder, Mac.InstallRoot(Path.Combine(folder, "EasyVocal"))); // a Mac build not inside a .app
        Assert.Equal(Path.Combine(app, "Contents", "MacOS", "EasyVocal"), Mac.ProgramIn(app));
        Assert.Equal(Path.Combine(folder, "EasyVocal"), Linux.ProgramIn(folder));
    }

    [Fact]
    public void CanSelfUpdate_WhereTheFolderCanBeWritten_NotFromATranslocatedCopy()
    {
        var folder = Path.Combine(temp, "opt", "EasyVocal");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "easyvocal-files.txt"), Marker);
        Assert.True(Linux.CanSelfUpdate(Path.Combine(folder, "EasyVocal")));
        Assert.True(Linux.IsInstallRoot(folder));
        var why = new List<string>();
        Assert.False(Mac.CanSelfUpdate("/private/var/folders/x/AppTranslocation/1234/d/EasyVocal.app/Contents/MacOS/EasyVocal", why.Add));
        Assert.Contains("Translocation", why.Single());
        Assert.False(Linux.CanSelfUpdate(Path.Combine(temp, "missing", "EasyVocal", "EasyVocal")));
    }

    /// <summary>
    /// A program run loose (from Downloads, next to the person's files): its folder is not an install, so the updater
    /// never offers to replace it (the release page opens) and the stale-copy cleanup deletes nothing.
    /// </summary>
    [Fact]
    public void ALooseProgram_IsNeverUpdatedInPlace_AndNothingAroundItIsDeleted()
    {
        var downloads = Path.Combine(temp, "home", "Downloads");
        Directory.CreateDirectory(downloads);
        var exe = Path.Combine(downloads, "EasyVocal");
        File.WriteAllBytes(exe, Elf);
        File.WriteAllText(Path.Combine(downloads, "holiday.jpg"), "the person's own file");
        Directory.CreateDirectory(downloads + ".old");
        File.WriteAllText(Path.Combine(downloads + ".old", "keep.txt"), "also theirs");
        var why = new List<string>();
        Assert.False(Linux.CanSelfUpdate(exe, why.Add));
        Assert.Contains("isn't called EasyVocal", why.Single());

        // Even with a marker lying around, a folder not named EasyVocal is refused; and a right name needs the marker.
        File.WriteAllBytes(Path.Combine(downloads, "easyvocal-files.txt"), Marker);
        Assert.False(Linux.CanSelfUpdate(exe));
        var named = Path.Combine(temp, "home", "EasyVocal");
        Directory.CreateDirectory(named);
        File.WriteAllBytes(Path.Combine(named, "EasyVocal"), Elf);
        Assert.False(Linux.CanSelfUpdate(Path.Combine(named, "EasyVocal")));
        Assert.False(Linux.IsInstallRoot(downloads + ".old")); // what CleanStale would delete: not a copy of EasyVocal

        // The same through the service: no install, and the cleanup leaves the person's folders alone.
        var service = new UpdateService(Linux, new Tests.Helpers.FakeUpdateSource(), new Tests.Helpers.FakeHelperLauncher(), new AppVersion(1, 0, 0), exe,
            Path.Combine(temp, "work"));
        service.CleanStale();
        Assert.False(service.CanSelfUpdate());
        Assert.Equal(UpdateProblem.FolderNotWritable, Assert.Throws<UpdateException>(() => service.StartInstall(Path.Combine(temp, "work", "a", "new", "EasyVocal"), 1, Path.Combine(temp, "u.log"), "")).Problem);
        Assert.True(File.Exists(Path.Combine(downloads, "holiday.jpg")));
        Assert.True(File.Exists(Path.Combine(downloads + ".old", "keep.txt")));
    }

    [Fact]
    public void Mac_OnlyTheAppsOwnProgram_InAMarkedBundle()
    {
        var app = Path.Combine(temp, "Applications", "EasyVocal.app");
        var exe = Path.Combine(app, "Contents", "MacOS", "EasyVocal");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, MachO);
        Assert.Contains("easyvocal-files.txt", Mac.NotAnInstall(exe));
        Directory.CreateDirectory(Path.Combine(app, "Contents", "Resources"));
        File.WriteAllBytes(Path.Combine(app, "Contents", "Resources", "easyvocal-files.txt"), Marker);
        Assert.Null(Mac.NotAnInstall(exe));
        Assert.True(Mac.CanSelfUpdate(exe));
        Assert.True(Mac.IsInstallRoot(app));
        // Another program inside the bundle (a helper tool) is not EasyVocal itself.
        Assert.NotNull(Mac.NotAnInstall(Path.Combine(app, "Contents", "MacOS", "other")));
        // Not from an .app at all.
        Assert.NotNull(Mac.NotAnInstall(Path.Combine(temp, "Downloads", "EasyVocal")));
        Assert.True(InstallLayout.IsTranslocated("/private/var/folders/x/AppTranslocation/1/d/EasyVocal.app"));
        Assert.False(InstallLayout.IsTranslocated(app));
    }

    private string Zip(string name, params (string Entry, byte[] Data)[] files)
    {
        var path = Path.Combine(temp, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in files)
        {
            var e = zip.CreateEntry(entry);
            e.ExternalAttributes = (entry.EndsWith("/EasyVocal") ? 0x81ED : 0x81A4) << 16;
            using var s = e.Open();
            s.Write(data);
        }
        return path;
    }

    private string TarGz(string name, params (string Entry, byte[] Data)[] files)
    {
        var path = Path.Combine(temp, name);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
        foreach (var (entry, data) in files)
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry) { DataStream = new MemoryStream(data), Mode = (UnixFileMode)0x1ED });
        return path;
    }

    [Fact]
    public void Unpack_GivesTheNewAppOrFolder_AfterCheckingItsMarkerAndProgram()
    {
        const string AppMarker = "EasyVocal.app/Contents/Resources/easyvocal-files.txt";
        var mac = Mac.Unpack(Zip("mac.zip", ("EasyVocal.app/Contents/MacOS/EasyVocal", Universal), ("EasyVocal.app/Contents/Info.plist", new byte[] { 1 }),
            (AppMarker, Marker)), Path.Combine(temp, "u1"), default);
        Assert.Equal(Path.Combine(temp, "u1", "EasyVocal.app"), mac);
        var nested = Mac.Unpack(Zip("mac2.zip", ("EasyVocal-1.0.0/EasyVocal.app/Contents/MacOS/EasyVocal", MachO), ("EasyVocal-1.0.0/" + AppMarker, Marker)),
            Path.Combine(temp, "u2"), default);
        Assert.EndsWith(Path.Combine("EasyVocal-1.0.0", "EasyVocal.app"), nested);
        var linux = Linux.Unpack(TarGz("l.tar.gz", ("EasyVocal/EasyVocal", Elf), ("EasyVocal/libportaudio.so", new byte[] { 2 }), ("EasyVocal/easyvocal-files.txt", Marker)),
            Path.Combine(temp, "u3"), default);
        Assert.Equal(Path.Combine(temp, "u3", "EasyVocal"), linux);

        void Fails(UpdateProblem problem, Func<string> unpack) => Assert.Equal(problem, Assert.Throws<UpdateException>(() => unpack()).Problem);
        Fails(UpdateProblem.NotAnExe, () => Mac.Unpack(Zip("elf.zip", ("EasyVocal.app/Contents/MacOS/EasyVocal", Elf), (AppMarker, Marker)), Path.Combine(temp, "u4"), default));
        Fails(UpdateProblem.NotAnExe, () => Linux.Unpack(TarGz("macho.tar.gz", ("EasyVocal/EasyVocal", MachO), ("EasyVocal/easyvocal-files.txt", Marker)), Path.Combine(temp, "u5"), default));
        Fails(UpdateProblem.MissingExe, () => Linux.Unpack(TarGz("other.tar.gz", ("Other/EasyVocal", Elf)), Path.Combine(temp, "u6"), default));
        Fails(UpdateProblem.MissingExe, () => Mac.Unpack(Zip("noexe.zip", ("EasyVocal.app/Contents/Info.plist", new byte[] { 1 })), Path.Combine(temp, "u7"), default));
        // Without the release's marker it is not a release of ours.
        Fails(UpdateProblem.MissingExe, () => Linux.Unpack(TarGz("nomarker.tar.gz", ("EasyVocal/EasyVocal", Elf)), Path.Combine(temp, "u8"), default));
        Fails(UpdateProblem.MissingExe, () => Mac.Unpack(Zip("nomarker.zip", ("EasyVocal.app/Contents/MacOS/EasyVocal", MachO)), Path.Combine(temp, "u9"), default));
    }

    [Fact]
    public void Helper_QuotesEveryPath_AndKnowsEachSystem()
    {
        var plan = new HelperPlan(4321, "/Applications/Easy'Vocal.app", "/tmp/w/a b/new/EasyVocal.app", "/tmp/w/a b", "/Users/x/Library/Application Support/EasyVocal/update.log",
            Relaunch: true, ShHelperScript.RelaunchArguments("/Users/x/my data/"));
        var mac = ShHelperScript.Build(plan, mac: true);
        Assert.StartsWith("#!/bin/sh\n", mac);
        Assert.Contains("WAIT_PID=4321\n", mac);
        Assert.Contains("TARGET='/Applications/Easy'\\''Vocal.app'\n", mac);
        Assert.Contains("NEW='/tmp/w/a b/new/EasyVocal.app'\n", mac);
        Assert.Contains("STAGING='/tmp/w/a b'\n", mac);
        Assert.Contains("PROGRAM=\"$TARGET/Contents/MacOS/EasyVocal\"", mac);
        Assert.Contains("MARKER='Contents/Resources/easyvocal-files.txt'\n", mac);
        Assert.Contains("[ ! -f \"$TARGET/$MARKER\" ]", mac);
        Assert.Contains("xattr -dr com.apple.quarantine \"$TARGET\"", mac);
        Assert.Contains("codesign --force --deep --sign - \"$TARGET\"", mac);
        Assert.True(mac.IndexOf("xattr", StringComparison.Ordinal) < mac.IndexOf("codesign --force", StringComparison.Ordinal));
        Assert.Contains("/usr/bin/open \"$TARGET\" --args --data '/Users/x/my data'", mac);
        Assert.Contains("BACKUP=\"$TARGET.old\"", mac);
        Assert.Contains("chmod +x \"$PROGRAM\"", mac);
        Assert.DoesNotContain("\r", mac);

        var linux = ShHelperScript.Build(plan with { Target = "/opt/EasyVocal", NewPayload = "/tmp/w/a b/new/EasyVocal" }, mac: false);
        Assert.DoesNotContain("xattr", linux);
        Assert.DoesNotContain("codesign", linux);
        Assert.DoesNotContain("/usr/bin/open", linux);
        Assert.Contains("PROGRAM=\"$TARGET/EasyVocal\"", linux);
        Assert.Contains("MARKER='easyvocal-files.txt'\n", linux);
        Assert.Contains("nohup \"$PROGRAM\" --data '/Users/x/my data' >/dev/null 2>&1 &", linux);
        var launch = ShLauncher.Arguments("/tmp/w/update.sh");
        Assert.Equal(new[] { "-c", "sh", "/tmp/w/update.sh" }, new[] { launch[0], launch[2], launch[3] });
        Assert.Contains("setsid /bin/sh \"$1\"", launch[1]);
        Assert.Contains("nohup /bin/sh \"$1\"", launch[1]);
        Assert.Equal("", ShHelperScript.RelaunchArguments(null));
    }

    /// <summary>An installed EasyVocal folder and a downloaded one, both with the release's marker.</summary>
    private (string Target, string Staging, string Payload) Install(string where)
    {
        var target = Path.Combine(temp, where, "EasyVocal");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "EasyVocal"), "old");
        File.WriteAllBytes(Path.Combine(target, "easyvocal-files.txt"), Marker);
        File.WriteAllText(Path.Combine(target, "old-only.txt"), "gone after the update");
        var staging = Path.Combine(temp, "work-" + Guid.NewGuid().ToString("N")[..6]);
        var payload = Path.Combine(staging, "new", "EasyVocal");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "EasyVocal"), "new");
        File.WriteAllBytes(Path.Combine(payload, "easyvocal-files.txt"), Marker);
        return (target, staging, payload);
    }

    /// <summary>Runs the helper with /bin/sh (macOS, Linux): swaps the folder, keeps nothing old, puts the old one back on failure.</summary>
    [Fact]
    public void Real_Helper_SwapsTheFolder_OrPutsTheOldOneBack()
    {
        if (OperatingSystem.IsWindows())
        {
            output.WriteLine("SKIPPED on Windows: the sh helper runs on macOS and Linux");
            return;
        }
        var (target, staging, payload) = Install(Path.Combine("opt", "Easy Vocal's"));
        var log = Path.Combine(temp, "update.log");

        int code = RunHelper(new HelperPlan(0, target, payload, staging, log, Relaunch: false, ""), Path.Combine(temp, "update1.sh"));
        Assert.Equal(0, code);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "EasyVocal")));
        Assert.False(File.Exists(Path.Combine(target, "old-only.txt")));
        Assert.False(Directory.Exists(target + ".old"));
        Assert.False(Directory.Exists(staging));
        Assert.True(File.GetUnixFileMode(Path.Combine(target, "EasyVocal")).HasFlag(UnixFileMode.UserExecute));
        Assert.Contains("updated", File.ReadAllText(log));

        // The new version can't be moved in (its folder is read-only): the old one is put back, exit 3.
        var (_, staging2, payload2) = Install("unused");
        var lockedFolder = Path.GetDirectoryName(payload2)!;
        File.SetUnixFileMode(lockedFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            int failed = RunHelper(new HelperPlan(0, target, payload2, null, log, Relaunch: false, ""), Path.Combine(temp, "update2.sh"));
            Assert.Equal(3, failed);
        }
        finally { File.SetUnixFileMode(lockedFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "EasyVocal")));
        Assert.False(Directory.Exists(target + ".old"));
        Assert.Contains("put back", File.ReadAllText(log));
        TestFolders.Delete(staging2);
    }

    /// <summary>A target without the release's marker (a loose program's folder: Downloads) is never touched.</summary>
    [Fact]
    public void Real_Helper_RefusesAFolderThatIsNotAnInstall()
    {
        if (OperatingSystem.IsWindows())
        {
            output.WriteLine("SKIPPED on Windows: the sh helper runs on macOS and Linux");
            return;
        }
        var downloads = Path.Combine(temp, "Downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads, "EasyVocal"), "loose");
        File.WriteAllText(Path.Combine(downloads, "holiday.jpg"), "the person's own file");
        var (_, staging, payload) = Install("unused2");
        var log = Path.Combine(temp, "update3.log");
        int code = RunHelper(new HelperPlan(0, downloads, payload, staging, log, Relaunch: false, ""), Path.Combine(temp, "update3.sh"));
        Assert.Equal(5, code);
        Assert.True(File.Exists(Path.Combine(downloads, "holiday.jpg")));
        Assert.Equal("loose", File.ReadAllText(Path.Combine(downloads, "EasyVocal")));
        Assert.False(Directory.Exists(downloads + ".old"));
        Assert.Contains("not an EasyVocal install", File.ReadAllText(log));
    }

    [Fact]
    public void Real_Helper_WaitsForEasyVocalToClose()
    {
        if (OperatingSystem.IsWindows())
        {
            output.WriteLine("SKIPPED on Windows: the sh helper runs on macOS and Linux");
            return;
        }
        var (target, _, payload) = Install("app");
        using var running = Process.Start(new ProcessStartInfo("/bin/sleep", "1.5") { UseShellExecute = false })!;
        var watch = Stopwatch.StartNew();
        int code = RunHelper(new HelperPlan(running.Id, target, payload, null, Path.Combine(temp, "u.log"), Relaunch: false, ""), Path.Combine(temp, "u.sh"));
        Assert.Equal(0, code);
        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(1), "it waited for the program to end (" + watch.Elapsed + ")");
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "EasyVocal")));
    }

    private int RunHelper(HelperPlan plan, string script)
    {
        File.WriteAllText(script, ShHelperScript.Build(plan, mac: false), Linux.HelperEncoding);
        using var p = Process.Start(new ProcessStartInfo("/bin/sh", new[] { script }) { UseShellExecute = false, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30000), "the helper finished");
        if (err.Length > 0) output.WriteLine("stderr: " + err);
        return p.ExitCode;
    }

    [Fact]
    public void LooksLikeProgram_MachOAndElf()
    {
        var file = Path.Combine(temp, "p");
        foreach (var (bytes, mac, expected) in new[] { (MachO, true, true), (Universal, true, true), (Elf, true, false), (Elf, false, true), (MachO, false, false) })
        {
            File.WriteAllBytes(file, bytes);
            Assert.Equal(expected, UnixUpdaterPlatform.LooksLikeProgram(file, mac));
        }
    }
}
