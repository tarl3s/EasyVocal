using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using EasyVocal.Core.Update;
using EasyVocal.Platform.Unix;
using Xunit.Abstractions;

namespace EasyVocal.Tests.Platforms;

/// <summary>The macOS/Linux updater: release names, what is replaced, unpacking, and the sh helper (run for real on macOS/Linux).</summary>
public class UnixUpdaterTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string temp = TestFolders.New("EV_Test_UnixUpdate_");
    private static readonly byte[] MachO = { 0xCF, 0xFA, 0xED, 0xFE, 7, 0, 0, 1 };
    private static readonly byte[] Universal = { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2 };
    private static readonly byte[] Elf = { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0 };

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
        Assert.True(Linux.CanSelfUpdate(Path.Combine(folder, "EasyVocal")));
        var why = new List<string>();
        Assert.False(Mac.CanSelfUpdate("/private/var/folders/x/AppTranslocation/1234/d/EasyVocal.app/Contents/MacOS/EasyVocal", why.Add));
        Assert.Contains("Translocation", why.Single());
        Assert.False(Linux.CanSelfUpdate(Path.Combine(temp, "missing", "EasyVocal", "EasyVocal")));
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
    public void Unpack_GivesTheNewAppOrFolder_AfterCheckingItsProgram()
    {
        var mac = Mac.Unpack(Zip("mac.zip", ("EasyVocal.app/Contents/MacOS/EasyVocal", Universal), ("EasyVocal.app/Contents/Info.plist", new byte[] { 1 })),
            Path.Combine(temp, "u1"), default);
        Assert.Equal(Path.Combine(temp, "u1", "EasyVocal.app"), mac);
        var nested = Mac.Unpack(Zip("mac2.zip", ("EasyVocal-1.0.0/EasyVocal.app/Contents/MacOS/EasyVocal", MachO)), Path.Combine(temp, "u2"), default);
        Assert.EndsWith(Path.Combine("EasyVocal-1.0.0", "EasyVocal.app"), nested);
        var linux = Linux.Unpack(TarGz("l.tar.gz", ("EasyVocal/EasyVocal", Elf), ("EasyVocal/libportaudio.so", new byte[] { 2 })), Path.Combine(temp, "u3"), default);
        Assert.Equal(Path.Combine(temp, "u3", "EasyVocal"), linux);

        void Fails(UpdateProblem problem, Func<string> unpack) => Assert.Equal(problem, Assert.Throws<UpdateException>(() => unpack()).Problem);
        Fails(UpdateProblem.NotAnExe, () => Mac.Unpack(Zip("elf.zip", ("EasyVocal.app/Contents/MacOS/EasyVocal", Elf)), Path.Combine(temp, "u4"), default));
        Fails(UpdateProblem.NotAnExe, () => Linux.Unpack(TarGz("macho.tar.gz", ("EasyVocal/EasyVocal", MachO)), Path.Combine(temp, "u5"), default));
        Fails(UpdateProblem.MissingExe, () => Linux.Unpack(TarGz("other.tar.gz", ("Other/EasyVocal", Elf)), Path.Combine(temp, "u6"), default));
        Fails(UpdateProblem.MissingExe, () => Mac.Unpack(Zip("noexe.zip", ("EasyVocal.app/Contents/Info.plist", new byte[] { 1 })), Path.Combine(temp, "u7"), default));
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
        Assert.Contains("xattr -dr com.apple.quarantine \"$TARGET\"", mac);
        Assert.Contains("/usr/bin/open \"$TARGET\" --args --data '/Users/x/my data'", mac);
        Assert.Contains("BACKUP=\"$TARGET.old\"", mac);
        Assert.Contains("chmod +x \"$PROGRAM\"", mac);
        Assert.DoesNotContain("\r", mac);

        var linux = ShHelperScript.Build(plan with { Target = "/opt/EasyVocal", NewPayload = "/tmp/w/a b/new/EasyVocal" }, mac: false);
        Assert.DoesNotContain("xattr", linux);
        Assert.DoesNotContain("/usr/bin/open", linux);
        Assert.Contains("PROGRAM=\"$TARGET/EasyVocal\"", linux);
        Assert.Contains("nohup \"$PROGRAM\" --data '/Users/x/my data' >/dev/null 2>&1 &", linux);
        Assert.Equal(new[] { "-c", "nohup /bin/sh \"$1\" >/dev/null 2>&1 &", "sh", "/tmp/w/update.sh" }, ShLauncher.Arguments("/tmp/w/update.sh"));
        Assert.Equal("", ShHelperScript.RelaunchArguments(null));
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
        // Installed: /…/opt/EasyVocal with the old program; downloaded: /…/work/attempt/new/EasyVocal.
        var target = Path.Combine(temp, "opt", "Easy Vocal's", "EasyVocal");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "EasyVocal"), "old");
        File.WriteAllText(Path.Combine(target, "old-only.txt"), "gone after the update");
        var staging = Path.Combine(temp, "work", "attempt");
        var payload = Path.Combine(staging, "new", "EasyVocal");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "EasyVocal"), "new");
        var log = Path.Combine(temp, "update.log");

        int code = RunHelper(new HelperPlan(0, target, payload, staging, log, Relaunch: false, ""), Path.Combine(temp, "update1.sh"));
        Assert.Equal(0, code);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "EasyVocal")));
        Assert.False(File.Exists(Path.Combine(target, "old-only.txt")));
        Assert.False(Directory.Exists(target + ".old"));
        Assert.False(Directory.Exists(staging));
        Assert.True(File.GetUnixFileMode(Path.Combine(target, "EasyVocal")).HasFlag(UnixFileMode.UserExecute));
        Assert.Contains("updated", File.ReadAllText(log));

        // The new version is missing (the copy failed): the old one is put back, exit 3.
        int failed = RunHelper(new HelperPlan(0, target, Path.Combine(temp, "nothing-here"), null, log, Relaunch: false, ""), Path.Combine(temp, "update2.sh"));
        Assert.Equal(3, failed);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "EasyVocal")));
        Assert.False(Directory.Exists(target + ".old"));
        Assert.Contains("put back", File.ReadAllText(log));
    }

    [Fact]
    public void Real_Helper_WaitsForEasyVocalToClose()
    {
        if (OperatingSystem.IsWindows())
        {
            output.WriteLine("SKIPPED on Windows: the sh helper runs on macOS and Linux");
            return;
        }
        var target = Path.Combine(temp, "app");
        Directory.CreateDirectory(target);
        var payload = Path.Combine(temp, "work", "a", "new");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "EasyVocal"), "new");
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
