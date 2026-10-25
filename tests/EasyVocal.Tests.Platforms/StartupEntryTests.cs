using EasyVocal.Platform.Linux;
using EasyVocal.Platform.MacOS;

namespace EasyVocal.Tests.Platforms;

/// <summary>"Open/Start at login" and the applications menu entry, in throwaway folders (never the real ones).</summary>
public class StartupEntryTests : IDisposable
{
    private readonly string temp = TestFolders.New("EV_Test_Startup_");

    public void Dispose() => TestFolders.Delete(temp);

    [Fact]
    public void LaunchAgent_StartsEasyVocalHiddenAtLogin()
    {
        var plist = LaunchAgent.BuildPlist("/Applications/Easy & Vocal.app/Contents/MacOS/EasyVocal");
        Assert.Contains("<string>com.tarl3s.easyvocal</string>", plist);
        Assert.Contains("<string>/Applications/Easy &amp; Vocal.app/Contents/MacOS/EasyVocal</string>", plist); // XML-escaped
        Assert.Contains("<string>--tray</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>\n    <true/>", plist);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", plist);
        Assert.Equal("com.tarl3s.easyvocal.plist", LaunchAgent.FileName);

        var agents = Path.Combine(temp, "LaunchAgents");
        var agent = new LaunchAgent(agents, "/Applications/EasyVocal.app/Contents/MacOS/EasyVocal");
        Assert.False(agent.IsEnabled());
        agent.Set(true);
        Assert.True(agent.IsEnabled());
        Assert.Equal(LaunchAgent.BuildPlist("/Applications/EasyVocal.app/Contents/MacOS/EasyVocal"), File.ReadAllText(agent.PlistPath));
        Assert.False(agent.Refresh()); // already right
        var moved = new LaunchAgent(agents, "/Users/x/Applications/EasyVocal.app/Contents/MacOS/EasyVocal");
        Assert.True(moved.Refresh()); // EasyVocal moved: written again
        Assert.Contains("/Users/x/Applications/", File.ReadAllText(agent.PlistPath));
        agent.Set(false);
        Assert.False(agent.IsEnabled());
        agent.Set(false); // nothing there: nothing happens
    }

    [Fact]
    public void DesktopExec_QuotesAndReadsBack()
    {
        Assert.Equal("/opt/EasyVocal/EasyVocal", DesktopFile.ExecArgument("/opt/EasyVocal/EasyVocal"));
        Assert.Equal("--tray", DesktopFile.ExecArgument("--tray"));
        Assert.Equal("\"/home/a b/EasyVocal\"", DesktopFile.ExecArgument("/home/a b/EasyVocal"));
        Assert.Equal("\"/x/\\\\$HOME/\\\\\"q\\\\\"\"", DesktopFile.ExecArgument("/x/$HOME/\"q\""));
        Assert.Equal("\"/100%%/x y\"", DesktopFile.ExecArgument("/100%/x y"));
        foreach (var path in new[] { "/opt/EasyVocal/EasyVocal", "/home/João Silva/Área de Trabalho/EasyVocal/EasyVocal", "/x/$a`b\"c\\d/100%/E" })
        {
            var exec = DesktopFile.ExecArgument(path) + " --tray";
            Assert.Equal(path, DesktopFile.ProgramOf(exec));
        }
    }

    [Fact]
    public void Autostart_StartsEasyVocalHiddenAtLogin()
    {
        var folder = Path.Combine(temp, "autostart");
        var entry = new AutostartEntry(folder, "/home/me/EasyVocal/EasyVocal");
        Assert.False(entry.IsEnabled());
        entry.Set(true);
        var text = File.ReadAllText(Path.Combine(folder, "easyvocal.desktop"));
        Assert.Contains("[Desktop Entry]\nType=Application\n", text);
        Assert.Contains("\nExec=/home/me/EasyVocal/EasyVocal --tray\n", text);
        Assert.Contains("\nX-GNOME-Autostart-enabled=true\n", text);
        Assert.DoesNotContain("Categories=", text);
        Assert.False(entry.Refresh());
        Assert.True(new AutostartEntry(folder, "/home/me/new place/EasyVocal").Refresh());
        Assert.Contains("Exec=\"/home/me/new place/EasyVocal\" --tray", File.ReadAllText(entry.EntryPath));
        entry.Set(false);
        Assert.False(entry.IsEnabled());
    }

    [Fact]
    public void AppMenuEntry_WithEasyVocalsIcon()
    {
        var ico = File.ReadAllBytes(Path.Combine(TestFolders.RepoRoot(), "src", "EasyVocal", "Assets", "EasyVocal.ico"));
        var png = DesktopFile.LargestPngInIco(ico);
        Assert.NotNull(png);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png![..4]);
        Assert.Null(DesktopFile.LargestPngInIco(new byte[] { 1, 2, 3 }));

        var menu = new AppMenuEntry(Path.Combine(temp, "share"), "/home/me/EasyVocal/EasyVocal", () => ico);
        Assert.True(menu.Supported);
        Assert.False(menu.Exists());
        Assert.Null(menu.ReadTarget());
        Assert.True(menu.Refresh());
        Assert.True(menu.Exists());
        Assert.Equal("/home/me/EasyVocal/EasyVocal", menu.ReadTarget());
        Assert.Equal(png, File.ReadAllBytes(menu.IconPath));
        var text = File.ReadAllText(menu.ShortcutPath);
        Assert.Contains("\nIcon=" + menu.IconPath.Replace("\\", "\\\\") + "\n", text); // (a backslash, on Windows, is written doubled)
        Assert.Contains("\nCategories=AudioVideo;Audio;\n", text);
        Assert.EndsWith(Path.Combine("share", "applications", "easyvocal.desktop"), menu.ShortcutPath);
        int size = DesktopFile.PngWidth(png);
        Assert.EndsWith(Path.Combine("icons", "hicolor", size + "x" + size, "apps", "easyvocal.png"), menu.IconPath); // the folder of its size

        var moved = new AppMenuEntry(Path.Combine(temp, "share"), "/opt/EasyVocal/EasyVocal", () => ico);
        Assert.True(moved.Refresh());
        Assert.Equal("/opt/EasyVocal/EasyVocal", moved.ReadTarget());
    }

    [Fact]
    public void Mac_HasNoMenuEntryToMake()
    {
        Assert.False(NoAppMenuEntry.Instance.Supported);
        Assert.False(NoAppMenuEntry.Instance.Refresh());
        Assert.Throws<PlatformNotSupportedException>(NoAppMenuEntry.Instance.Create);
    }

    [Fact]
    public void LaunchAgent_FromATranslocatedCopy_IsRefused_AndNeverOverwritesAGoodOne()
    {
        var agents = Path.Combine(temp, "LaunchAgents");
        var good = new LaunchAgent(agents, "/Applications/EasyVocal.app/Contents/MacOS/EasyVocal");
        good.Set(true);
        var plist = File.ReadAllText(good.PlistPath);
        var translocated = new LaunchAgent(agents, "/private/var/folders/ab/AppTranslocation/1234-5678/d/EasyVocal.app/Contents/MacOS/EasyVocal");
        Assert.False(translocated.Refresh()); // a run from the temporary copy leaves the login item alone
        Assert.Equal(plist, File.ReadAllText(good.PlistPath));
        var refused = Assert.Throws<InvalidOperationException>(() => translocated.Set(true));
        Assert.Contains("Applications", refused.Message);
        Assert.Equal(plist, File.ReadAllText(good.PlistPath));
        translocated.Set(false); // turning it off still works
        Assert.False(good.IsEnabled());
    }

    [Fact]
    public void PngWidth_ReadsTheHeader()
    {
        var png = new byte[32];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 2, 0, 0, 0, 2, 0 }.CopyTo(png, 0);
        Assert.Equal(512, DesktopFile.PngWidth(png));
        Assert.Equal(256, DesktopFile.PngWidth(new byte[3]));
    }
}
