using System.IO;
using System.IO.Compression;
using EasyVocal.Core.Engine;
using Microsoft.Win32;

namespace EasyVocal.Tests;

/// <summary>"Start with Windows" (on a throwaway registry key, never the real Run key) and the VB-CABLE pack handling.</summary>
public class StartupRegistrationTests : IDisposable
{
    private const string TestRoot = @"Software\EasyVocalTest";
    private readonly string keyPath = TestRoot + @"\Run_" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);

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
}

public class DeviceListFilterTests
{
    private static DeviceInfo D(string name) => new(name.ToLowerInvariant(), name, false);

    [Fact]
    public void ExcludeVirtualCable_DropsBothEnds_KeepsTheRestInOrder()
    {
        var kept = DeviceRules.ExcludeVirtualCable(new[]
        {
            D("Microphone (Realtek)"), D("CABLE Output (VB-Audio Virtual Cable)"), D("Headset (USB)"), D("CABLE Input (VB-Audio Virtual Cable)"),
        });
        Assert.Equal(new[] { "Microphone (Realtek)", "Headset (USB)" }, kept.Select(d => d.Name));
    }
}
