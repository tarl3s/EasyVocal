using System.IO;
using EasyVocal.Platform.Windows;

namespace EasyVocal.Tests;

/// <summary>The Start menu shortcut, written to a throwaway folder (never the real Start menu).</summary>
public class StartMenuShortcutTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "EV_Test_StartMenu_" + Guid.NewGuid().ToString("N"));
    private readonly string exe = Path.Combine(Path.GetTempPath(), "EV_Test_StartMenu_Apps", "EasyVocal.exe");

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void DefaultFolder_IsTheUsersStartMenuPrograms()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "EasyVocal.lnk"),
            StartMenuShortcut.Default.ShortcutPath);
    }

    [Fact]
    public void Create_ThenReadTarget_OpensTheProgram()
    {
        var shortcut = new StartMenuShortcut(folder, exe);
        Assert.False(shortcut.Exists());
        Assert.Null(shortcut.ReadTarget());

        shortcut.Create();

        Assert.True(shortcut.Exists());
        Assert.Equal(exe, shortcut.ReadTarget(), ignoreCase: true);
    }

    [Fact]
    public void Refresh_RewritesAShortcutThatOpensAnotherPlace()
    {
        new StartMenuShortcut(folder, @"C:\Old place\EasyVocal.exe").Create();

        var moved = new StartMenuShortcut(folder, exe);
        Assert.Null(moved.Refresh());

        Assert.Equal(exe, moved.ReadTarget(), ignoreCase: true);
    }

    [Fact]
    public void Refresh_CreatesAMissingShortcut()
    {
        var shortcut = new StartMenuShortcut(folder, exe);
        Assert.Null(shortcut.Refresh());
        Assert.True(shortcut.Exists());
    }
}
