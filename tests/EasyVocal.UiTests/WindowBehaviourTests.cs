using EasyVocal.Core.Text;
using EasyVocal.Core.Update;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using EasyVocal.Core.Sounds;
using EasyVocal.E2E;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.UiTests;

/// <summary>The main window's close-to-tray, and dialogs shown without an owner (from the tray, at a --tray start).</summary>
// One at a time with the other app tests: they share the process' current session, its error count and the log.
[Collection("App sessions")]
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
            TestFolders.Delete(Data);
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

    [AvaloniaFact]
    public void Dialog_OverAMinimisedMainWindow_BringsTheWindowBackFirst()
    {
        using var f = new Fixture();
        f.Main.CanHideToTray = false; // no tray: closing minimises
        f.Main.Show();
        f.Main.Close();
        Assert.Equal(WindowState.Minimized, f.Main.WindowState);

        var settings = new SettingsWindow(f.Controller);
        _ = settings.ShowAsync(null);
        Assert.Same(f.Main, settings.Owner);
        Assert.Equal(WindowState.Normal, f.Main.WindowState); // a modal dialog over a window nobody can see would block it
        settings.Close();
    }

    [AvaloniaFact]
    public void Setup_AnInstallerThatThrows_DoesNotBlockTheNextInstall()
    {
        using var f = new Fixture();
        int calls = 0;
        f.Controller.CableInstaller = (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("a broken installer");
        };
        var setup = new SetupWindow(f.Controller, () => false);
        _ = setup.ShowAsync(null);
        Ui.Click(setup.InstallButton);
        Assert.Equal(1, calls);
        Assert.True(setup.InstallButton.IsEnabled);
        Ui.Click(setup.InstallButton); // the "an installer runs" guard was let go
        Assert.Equal(2, calls);
        setup.Close();
    }

    [AvaloniaFact]
    public async Task UpdateDialog_EasyVocalQuitWhileDownloading_InstallsNothing()
    {
        using var f = new Fixture();
        var work = Path.Combine(f.Data, "work");
        var exe = Path.Combine(f.Data, "app", "EasyVocal-test");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, FakeUpdaterPlatform.Program);
        var zip = Path.Combine(f.Data, "release.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        using (var s = archive.CreateEntry("EasyVocal-test").Open())
            s.Write(FakeUpdaterPlatform.Program);
        var platform = new FakeUpdaterPlatform();
        var source = new FakeUpdateSource { ArchivePath = zip, Gate = new TaskCompletionSource() };
        var service = new UpdateService(platform, source, platform.Launcher, new AppVersion(0, 2, 0), exe, work);
        int quits = 0;
        var coordinator = new UpdateCoordinator(f.Controller, service, canCheck: true, canInstall: true, forceCheck: false,
            () => null, (_, _) => { }, () => quits++, Path.Combine(f.Data, "update.log"), "");
        var release = new ReleaseInfo(new AppVersion(9, 9, 0), "v9.9.0", "",
            new[] { new ReleaseAsset("EasyVocal-test.zip", "https://example.com/e.zip", 1000) }, false, false, "https://example.com");
        var offer = coordinator.OfferAsync(null, new UpdateCheck(UpdateStatus.Available, release, release.Assets[0]));
        var dialog = AppWindow.OpenWindows.OfType<UpdateWindow>().Single();
        Ui.Click(dialog.UpdateButton);
        f.Controller.Dispose(); // quit from the tray while it downloads
        source.Gate.SetResult();
        await offer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, quits);
        Assert.Empty(platform.FakeLauncher.Started);
        Assert.True(!Directory.Exists(work) || Directory.GetFileSystemEntries(work).Length == 0, "the download was discarded");
    }

    [Fact]
    public async Task FireAndForget_ReportsAFailureAtOnce_AndTellsWhenEverythingIsDone()
    {
        var failures = new List<Exception>();
        void OnFailed(Exception e) { lock (failures) failures.Add(e); }
        FireAndForget.Failed += OnFailed;
        try
        {
            var gate = new TaskCompletionSource();
            FireAndForget.Watch(Task.Run(async () => { await gate.Task; throw new InvalidOperationException("boom"); }), "A test task");
            FireAndForget.Watch(Task.FromCanceled(new CancellationToken(true)), "A cancelled task"); // not a failure
            Assert.False(await FireAndForget.SettleAsync(TimeSpan.FromMilliseconds(50)));
            gate.SetResult();
            Assert.True(await FireAndForget.SettleAsync(TimeSpan.FromSeconds(5)));
            lock (failures) Assert.Contains(failures, e => e.Message == "boom");
            lock (failures) Assert.Single(failures);
        }
        finally { FireAndForget.Failed -= OnFailed; }
    }

    /// <summary>The devices of the e2e test, but the system refuses the microphone (macOS / Windows privacy settings).</summary>
    private sealed class RefusedMic(TestAudioDevices inner) : EasyVocal.Core.Platform.IAudioBackend
    {
        public IReadOnlyList<EasyVocal.Core.Engine.DeviceInfo> Inputs() => inner.Inputs();
        public IReadOnlyList<EasyVocal.Core.Engine.DeviceInfo> Outputs() => inner.Outputs();
        public EasyVocal.Core.Engine.IAudioInput OpenMic(string id) => throw new EasyVocal.Core.Platform.MicPermissionDeniedException();
        public EasyVocal.Core.Engine.IAudioOutput OpenSpeaker(string id) => inner.OpenSpeaker(id);
        public EasyVocal.Core.Engine.DefaultIds Defaults(bool playback) => inner.Defaults(playback);
        public bool SetDefault(string id) => false;
        public event Action? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
    }

    [AvaloniaFact]
    public void MicRefused_TheBannerSaysWhereToAllowIt_AndOpensThatPage()
    {
        var data = TestFolders.New("EV_UiMicRefused_");
        int opened = 0;
        var platform = TestPlatform.Create(new RefusedMic(new TestAudioDevices(cablePresent: true)), new FakeUpdaterPlatform(), SoundDecoders.Default)
            with { OpenMicSettings = () => opened++, Texts = EasyVocal.Core.Platform.PlatformTexts.MacOS };
        var controller = new AppController(data, platform);
        var main = new MainWindow(controller);
        try
        {
            main.Show();
            controller.Start();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(controller.MicPermissionDenied);
            Assert.True(main.Banner.IsVisible);
            Assert.Equal(L.T(EasyVocal.Core.Platform.PlatformTexts.MacOS.MicBlocked), main.BannerText.Text);
            Assert.False(main.MicButton.IsVisible); // choosing another mic doesn't help: the system refuses every one
            Ui.Click(main.PrivacyButton);
            Assert.Equal(1, opened);
        }
        finally
        {
            main.AllowClose();
            main.Close();
            controller.Dispose();
            TestFolders.Delete(data);
        }
    }

    [AvaloniaFact]
    public void Settings_QuitButton_AsksTheSessionToQuit()
    {
        using var f = new Fixture();
        f.Main.Show();
        int quits = 0;
        f.Controller.QuitRequested += () => quits++;
        var settings = new SettingsWindow(f.Controller);
        var shown = settings.ShowAsync(null);
        Assert.Equal(L.T(f.Controller.Platform.Texts.StartAtLogin), settings.StartBox.Content);
        Ui.Click(settings.QuitButton);
        Assert.True(shown.IsCompleted);
        Assert.Equal(1, quits);
    }
}
