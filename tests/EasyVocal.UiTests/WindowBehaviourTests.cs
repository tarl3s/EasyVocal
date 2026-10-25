using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using EasyVocal.Core.Sounds;
using EasyVocal.E2E;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.UiTests;

/// <summary>The main window's close-to-tray, and dialogs shown without an owner (from the tray, at a --tray start).</summary>
public class WindowBehaviourTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly string Data = Path.Combine(Path.GetTempPath(), "EV_UiWindows_" + Guid.NewGuid().ToString("N"));
        public readonly AppController Controller;
        public readonly MainWindow Main;

        public Fixture()
        {
            Directory.CreateDirectory(Data);
            Controller = new AppController(Data, TestPlatform.Create(new TestAudioDevices(cablePresent: true), new FakeUpdaterPlatform(), SoundDecoders.Default));
            Main = new MainWindow(Controller);
            AppWindow.MainWindowSource = () => Main;
        }

        public void Dispose()
        {
            AppWindow.CloseAllExcept(Main);
            AppWindow.MainWindowSource = null;
            Main.AllowClose();
            Main.Close();
            Controller.Dispose();
            for (int attempt = 0; attempt < 10 && Directory.Exists(Data); attempt++)
            {
                try { Directory.Delete(Data, recursive: true); }
                catch (IOException) { Thread.Sleep(200); }
            }
        }
    }

    [AvaloniaFact]
    public void Close_HidesToTray_AndTheTrayBringsItBack()
    {
        using var f = new Fixture();
        int hidden = 0;
        f.Main.HiddenToTray += () => hidden++;
        f.Main.Show();

        f.Main.Close();
        Assert.False(f.Main.IsVisible);
        Assert.Equal(1, hidden);
        Assert.False(f.Controller.IsDisposed);
        f.Controller.MicMuted = true; // still working
        Assert.True(f.Controller.MicMuted);

        f.Main.ShowFromTray();
        Assert.True(f.Main.IsVisible);
        Assert.Equal(WindowState.Normal, f.Main.WindowState);

        f.Main.WindowState = WindowState.Minimized;
        f.Main.ShowFromTray();
        Assert.Equal(WindowState.Normal, f.Main.WindowState);
    }

    [AvaloniaFact]
    public void Close_WithoutATray_Minimises()
    {
        using var f = new Fixture();
        int hidden = 0;
        f.Main.HiddenToTray += () => hidden++;
        f.Main.CanHideToTray = false;
        f.Main.Show();

        f.Main.Close();
        Assert.True(f.Main.IsVisible);
        Assert.Equal(WindowState.Minimized, f.Main.WindowState);
        Assert.Equal(0, hidden);
    }

    [AvaloniaFact]
    public void OwnerlessDialog_GoesOverTheMainWindow_AndIsNeverOpenTwice()
    {
        using var f = new Fixture();
        f.Main.Show();

        var first = new SettingsWindow(f.Controller);
        var shown = first.ShowAsync(null); // e.g. opened from the tray while the window is up
        Assert.True(first.IsVisible);
        Assert.Same(f.Main, first.Owner); // modal over the main window

        var second = new SettingsWindow(f.Controller);
        var again = second.ShowAsync(null);
        Assert.True(second.WasDuplicate);
        Assert.False(second.IsVisible);
        Assert.Same(shown, again);
        Assert.Single(AppWindow.OpenWindows.OfType<SettingsWindow>());

        first.Close();
        Assert.True(shown.IsCompleted && again.IsCompleted);

        // Closed: the next one opens for real.
        var third = new SettingsWindow(f.Controller);
        _ = third.ShowAsync(null);
        Assert.False(third.WasDuplicate);
        Assert.True(third.IsVisible);
        third.Close();
    }

    [AvaloniaFact]
    public void OwnerlessDialog_WhileOnlyInTheTray_OpensOnItsOwn_OneSetupAtATime()
    {
        using var f = new Fixture(); // the main window is never shown: a --tray start

        var setup = new SetupWindow(f.Controller, () => true);
        var shown = setup.ShowAsync(null);
        Assert.True(setup.IsVisible);
        Assert.Null(setup.Owner);
        Assert.True(setup.ShowInTaskbar);

        f.Main.Show(); // the person opens the window from the tray and asks for the setup again
        var second = new SetupWindow(f.Controller, () => true);
        var again = second.ShowAsync(f.Main);
        Assert.True(second.WasDuplicate);
        Assert.Same(shown, again);
        Assert.Single(AppWindow.OpenWindows.OfType<SetupWindow>());
        setup.Close();
        Assert.True(again.IsCompleted);
    }
}
