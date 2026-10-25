using System.IO;
using System.IO.Compression;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;
using EasyVocal.Platform.Windows;
using Microsoft.Win32;

namespace EasyVocal.Tests;

/// <summary>"Start with Windows" (on a throwaway registry key, never the real Run key) and the VB-CABLE pack handling.</summary>
public class StartupRegistrationTests : IDisposable
{
    private const string TestRoot = @"Software\EasyVocalTest";
    private readonly string keyPath = TestRoot + @"\Run_" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        // Only this test's own subkey; the shared parent goes only when nobody else's key is left in it.
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        try { Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false); }
        catch (InvalidOperationException) { /* another test's key is still in it */ }
    }

    private StartupRegistration Make(string exe = @"C:\Apps\EasyVocal\EasyVocal.exe") => new(keyPath, "EasyVocalTest", exe);

    [Fact]
    public void RealRunKey_IsTheDefault_AndNotTheTestKey()
    {
        // Documents that the default instance targets Windows' real Run key; no test calls Set on it.
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.RunKeyPath);
        Assert.Equal("EasyVocal", StartupRegistration.DefaultValueName);
    }

    [Fact]
    public void BuildCommand_QuotesThePathAndAddsTray()
    {
        Assert.Equal("\"C:\\Program Files\\Easy Vocal\\EasyVocal.exe\" --tray",
            StartupRegistration.BuildCommand(@"C:\Program Files\Easy Vocal\EasyVocal.exe"));
    }

    [Fact]
    public void Set_On_WritesTheCommand_AndIsEnabled()
    {
        var s = Make();
        Assert.False(s.IsEnabled());
        s.Set(true);
        Assert.True(s.IsEnabled());
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        Assert.Equal("\"C:\\Apps\\EasyVocal\\EasyVocal.exe\" --tray", key!.GetValue("EasyVocalTest"));
    }

    [Fact]
    public void Set_Off_RemovesTheValue_AndWhenMissingDoesNothing()
    {
        var s = Make();
        s.Set(false); // no key at all: must not throw
        s.Set(true);
        s.Set(false);
        Assert.False(s.IsEnabled());
        s.Set(false);
        Assert.False(s.IsEnabled());
    }

    [Fact]
    public void Set_On_TwiceKeepsOneValue()
    {
        var s = Make();
        s.Set(true);
        s.Set(true);
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        Assert.Single(key!.GetValueNames());
    }

    [Fact]
    public void Refresh_RewritesAMovedProgram_AndLeavesACurrentOneAlone()
    {
        Make(@"C:\Old\EasyVocal.exe").Set(true);
        var now = Make(@"C:\New\EasyVocal.exe");
        Assert.True(now.IsEnabled());
        Assert.True(now.Refresh());
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            Assert.Equal("\"C:\\New\\EasyVocal.exe\" --tray", key!.GetValue("EasyVocalTest"));
        Assert.False(now.Refresh());
    }

    [Fact]
    public void Refresh_BringsBackAMissingValue()
    {
        var s = Make();
        Assert.True(s.Refresh());
        Assert.True(s.IsEnabled());
    }
}

public class VbCableInstallerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "EV_setup_" + Guid.NewGuid().ToString("N"));

    public VbCableInstallerTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string MakeZip(params (string Name, string Text)[] files)
    {
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        return path;
    }

    [Fact]
    public void Urls_ArePointedAtTheOfficialSite()
    {
        Assert.StartsWith("https://vb-audio.com/", VbCableInstaller.PageUrl);
        Assert.StartsWith("https://download.vb-audio.com/", VbCableInstaller.ZipUrl);
        Assert.EndsWith(".zip", VbCableInstaller.ZipUrl);
    }

    [Fact]
    public void ExtractZip_And_FindSetupExe_PickTheRightInstaller()
    {
        var zip = MakeZip(("VBCABLE_Setup_x64.exe", "64"), ("VBCABLE_Setup.exe", "32"), ("sub/readme.txt", "hi"));
        var dest = Path.Combine(folder, "pack");
        VbCableInstaller.ExtractZip(zip, dest);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(dest, "sub", "readme.txt")));
        Assert.Equal("VBCABLE_Setup_x64.exe", Path.GetFileName(VbCableInstaller.FindSetupExe(dest, true)));
        Assert.Equal("VBCABLE_Setup.exe", Path.GetFileName(VbCableInstaller.FindSetupExe(dest, false)));
    }

    [Fact]
    public void FindSetupExe_MissingFolderOrFile_ReturnsNull()
    {
        Assert.Null(VbCableInstaller.FindSetupExe(Path.Combine(folder, "nothing"), true));
        var zip = MakeZip(("other.exe", "x"));
        var dest = Path.Combine(folder, "pack2");
        VbCableInstaller.ExtractZip(zip, dest);
        Assert.Null(VbCableInstaller.FindSetupExe(dest, true));
    }

    [Fact]
    public void ExtractZip_RefusesAFileThatWouldLandOutsideTheFolder()
    {
        var zip = MakeZip(("..\\escape.txt", "bad"));
        var dest = Path.Combine(folder, "pack3");
        Assert.Throws<InvalidDataException>(() => VbCableInstaller.ExtractZip(zip, dest));
        Assert.False(File.Exists(Path.Combine(folder, "escape.txt")));
    }

    private static IProgress<double> NoProgress => new Progress<double>();

    private async Task<(InstallResult Result, string RunFolder, bool Launched)> Install(Func<string, SignatureCheck> verify,
        Func<string, CancellationToken, Task>? launch = null, CancellationToken ct = default)
    {
        var zip = MakeZip(("VBCABLE_Setup_x64.exe", "fake"), ("VBCABLE_Setup.exe", "fake"));
        var run = Path.Combine(folder, "run_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        bool launched = false;
        var result = await VbCableInstaller.InstallFromZipAsync(zip, run, NoProgress, ct, verify, launch ?? ((_, _) =>
        {
            launched = true;
            return Task.CompletedTask;
        }));
        return (result, run, launched);
    }

    [Fact]
    public async Task InstallFromZip_UnverifiedInstaller_IsNeverRun_AndFilesAreRemoved()
    {
        foreach (var bad in new[] { SignatureCheck.Untrusted, SignatureCheck.WrongSigner })
        {
            var (result, run, launched) = await Install(_ => bad);
            Assert.Equal(InstallStatus.NotVerified, result.Status);
            Assert.False(launched);
            Assert.False(Directory.Exists(run));
        }
    }

    [Fact]
    public async Task InstallFromZip_Verified_RunsTheInstaller_ThenRemovesTheFiles()
    {
        string? ran = null;
        var (result, run, _) = await Install(_ => SignatureCheck.Ok, (exe, _) =>
        {
            ran = exe;
            Assert.True(File.Exists(exe)); // still there while the installer runs
            return Task.CompletedTask;
        });
        Assert.Equal(InstallStatus.Installed, result.Status);
        Assert.EndsWith(Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe", ran);
        Assert.False(Directory.Exists(run));
    }

    [Fact]
    public async Task InstallFromZip_CancelledWhileTheInstallerRuns_KeepsItsFiles()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var task = Install(_ => SignatureCheck.Ok, async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct); // the elevated installer is still running
        }, cts.Token);
        await started.Task;
        cts.Cancel();
        var (result, run, _) = await task;
        Assert.Equal(InstallStatus.Cancelled, result.Status);
        Assert.True(File.Exists(Path.Combine(run, "pack", "VBCABLE_Setup_x64.exe")), "the installer's files must survive a cancel");
    }

    [Fact]
    public async Task InstallFromZip_PermissionDeclined_IsReportedAndCleansUp()
    {
        var (result, run, _) = await Install(_ => SignatureCheck.Ok, (_, _) => throw new System.ComponentModel.Win32Exception(1223));
        Assert.Equal(InstallStatus.DeclinedPermission, result.Status);
        Assert.False(Directory.Exists(run));
    }

    [Fact]
    public async Task InstallFromZip_NoInstallerInThePack_Fails()
    {
        var zip = MakeZip(("readme.txt", "x"));
        var run = Path.Combine(folder, "run_empty");
        Directory.CreateDirectory(run);
        var result = await VbCableInstaller.InstallFromZipAsync(zip, run, NoProgress, default, _ => SignatureCheck.Ok, (_, _) => Task.CompletedTask);
        Assert.Equal(InstallStatus.Failed, result.Status);
    }

    [Fact]
    public void CleanStale_RemovesOldRunFolders_AndKeepsFreshOnes()
    {
        var root = Path.Combine(folder, "root");
        var old = Path.Combine(root, "old");
        var fresh = Path.Combine(root, "fresh");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(fresh);
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        VbCableInstaller.CleanStale(root, TimeSpan.FromHours(6));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }
}

public class AuthenticodeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "EV_sig_" + Guid.NewGuid().ToString("N"));

    public AuthenticodeTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Theory]
    [InlineData("CN=VB-Audio Software, O=VB-Audio Software, L=Paris, C=FR", true)]
    [InlineData("CN=\"Vincent Burel\", C=FR", true)]
    [InlineData("O=Vincent Burel, CN=Something Else", true)]
    [InlineData("cn=vb-audio software", true)]
    [InlineData("CN=VB-Audio Software;O=Other", true)]
    [InlineData("CN=BUREL VINCENT Entrepreneur individuel, O=BUREL VINCENT Entrepreneur individuel, C=FR, SERIALNUMBER=423 734 177", true)] // the real VB-CABLE signer
    [InlineData("CN=burel vincent entrepreneur individuel", true)]
    [InlineData("CN=BUREL VINCENT Entrepreneur individuel Evil", false)]
    [InlineData("CN=Not BUREL VINCENT Entrepreneur individuel", false)]
    [InlineData("CN=BUREL VINCENT Entrepreneur", false)]
    [InlineData("CN=Evil, OU=BUREL VINCENT Entrepreneur individuel", false)]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, C=US", false)]
    [InlineData("CN=Not VB-Audio Software", false)]
    [InlineData("CN=VB-Audio Software.evil.example", false)]
    [InlineData("CN=Evil, OU=VB-Audio Software", false)] // only CN and O count
    [InlineData("CN=Evil, L=\"CN=VB-Audio Software\"", false)] // a quoted value can't fake a field
    [InlineData("", false)]
    [InlineData("VB-Audio Software", false)] // not a subject at all
    public void IsVbAudioSubject_MatchesWholeNamesInCnOrO(string subject, bool expected)
    {
        Assert.Equal(expected, Authenticode.IsVbAudioSubject(subject));
    }

    [Fact]
    public void ParseSubject_HandlesQuotesAndCommas()
    {
        var parts = Authenticode.ParseSubject("CN=A, O=\"B, Inc\", C=FR");
        Assert.Equal(new[] { ("CN", "A"), ("O", "B, Inc"), ("C", "FR") }, parts);
    }

    [Fact]
    public void IsTrusted_AnUnsignedFile_Fails()
    {
        var file = Path.Combine(folder, "unsigned.exe");
        File.WriteAllBytes(file, new byte[] { 0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0 }); // starts like a program, carries no signature
        Assert.False(Authenticode.IsTrusted(file));
        Assert.Equal(SignatureCheck.Untrusted, Authenticode.Check(file));
        File.WriteAllText(Path.Combine(folder, "text.txt"), "hello");
        Assert.False(Authenticode.IsTrusted(Path.Combine(folder, "text.txt")));
        Assert.False(Authenticode.IsTrusted(Path.Combine(folder, "missing.exe")));
    }

    [Fact]
    public void Check_ASignedMicrosoftProgram_IsTrusted_ButNotVbAudio()
    {
        var signed = SignedSample();
        Assert.True(Authenticode.IsTrusted(signed), signed + " should have a trusted embedded signature");
        Assert.Contains("Microsoft", Authenticode.SignerSubject(signed));
        Assert.Equal(SignatureCheck.WrongSigner, Authenticode.Check(signed));
    }

    [Fact]
    public void IsTrusted_ATamperedSignedProgram_Fails()
    {
        var copy = Path.Combine(folder, "tampered.exe");
        File.Copy(SignedSample(), copy);
        using (var f = new FileStream(copy, FileMode.Open, FileAccess.Write))
        {
            f.Seek(0x200, SeekOrigin.Begin); // inside the headers/code: changes the file hash, not the signature block
            f.WriteByte(0xFF);
        }
        Assert.False(Authenticode.IsTrusted(copy));
    }

    /// <summary>A Microsoft program with an embedded signature (many System32 files are only catalog-signed, which this check rejects).</summary>
    private static string SignedSample()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidates = new[]
        {
            Path.Combine(system, "notepad.exe"),
            Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Environment.ProcessPath ?? "",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c) && Authenticode.IsTrusted(c)) return c;
        Assert.Fail("No Windows program with an embedded trusted signature was found to test with.");
        return "";
    }
}

public class DeviceListFilterTests
{
    private static readonly CableNames Vb = CableNames.VbCable;

    private static DeviceInfo D(string name) => new(name.ToLowerInvariant(), name, false);

    [Fact]
    public void ExcludeVirtualCable_DropsBothEnds_KeepsTheRestInOrder()
    {
        var kept = DeviceRules.ExcludeVirtualCable(Vb, new[]
        {
            D("Microphone (Realtek)"), D("CABLE Output (VB-Audio Virtual Cable)"), D("Headset (USB)"), D("CABLE Input (VB-Audio Virtual Cable)"),
        });
        Assert.Equal(new[] { "Microphone (Realtek)", "Headset (USB)" }, kept.Select(d => d.Name));
    }
}
