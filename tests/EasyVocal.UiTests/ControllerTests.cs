using EasyVocal.Core.Platform;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Sounds;
using EasyVocal.E2E;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.UiTests;

/// <summary>
/// <see cref="AppController"/> without any UI framework: its posts and timers go to <see cref="ManualDispatcher"/>, which
/// the test pumps by hand.
/// </summary>
// One at a time with the other app tests: they share the process' current session, its error count and the log.
[Collection("App sessions")]
public class ControllerTests
{
    /// <summary>A UI thread the test drives: posted work waits in a queue, timers tick when told to.</summary>
    private sealed class ManualDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> posted = new();
        private readonly int thread = Environment.CurrentManagedThreadId;
        public readonly List<Timer> Timers = new();

        public bool CheckAccess() => Environment.CurrentManagedThreadId == thread;

        public void Post(Action action)
        {
            lock (posted) posted.Enqueue(action);
        }

        public IUiTimer CreateTimer(TimeSpan interval, Action tick)
        {
            var t = new Timer(interval, tick);
            Timers.Add(t);
            return t;
        }

        /// <summary>Runs what was posted so far (and what that posts in turn).</summary>
        public void Pump()
        {
            while (true)
            {
                Action? next;
                lock (posted) next = posted.Count > 0 ? posted.Dequeue() : null;
                if (next == null) return;
                next();
            }
        }

        /// <summary>Every running timer ticks once.</summary>
        public void TickAll()
        {
            foreach (var t in Timers.Where(t => t.Running).ToList()) t.Tick();
        }

        public sealed class Timer(TimeSpan interval, Action tick) : IUiTimer
        {
            public TimeSpan Interval { get; } = interval;
            public bool Running { get; private set; }
            public void Start() => Running = true;
            public void Stop() => Running = false;
            public void Tick() => tick();
        }
    }

    [Fact]
    public void Controller_RunsOnAnInjectedDispatcher_AndSavesThroughItsTimer()
    {
        var data = TestFolders.New("EV_UiController_");
        var ui = new ManualDispatcher();
        var devices = new TestAudioDevices(cablePresent: true);
        var platform = TestPlatform.Create(devices, new FakeUpdaterPlatform(), SoundDecoders.Default);
        try
        {
            using (var c = new AppController(data, platform, ui))
            {
                int changes = 0;
                c.Changed += () => changes++;
                c.Start();
                Assert.Equal(EngineState.Running, c.State);

                // A hotkey: registered with the (in-memory) system, its press handled on the UI thread right away.
                c.Settings.Voices.First(v => v.Id == "robot").Hotkey = "Ctrl+5";
                c.RebuildHotkeys();
                Assert.Equal(1, c.RegisteredHotkeyCount);
                Assert.True(c.SimulateHotkeyPress("voice:robot"));
                Assert.Equal("robot", c.ActiveVoice.Id);
                Assert.True(c.FilterEnabled);

                // A setting change is written by the save timer, not at once.
                c.MicMuted = true;
                Assert.True(changes > 0);
                var settings = Path.Combine(data, "settings.json");
                File.Delete(settings);
                Assert.Contains(ui.Timers, t => t.Running && t.Interval == TimeSpan.FromMilliseconds(500));
                ui.TickAll();
                ui.Pump();
                Assert.True(File.Exists(settings));
                Assert.Contains("\"MicMuted\": true", File.ReadAllText(settings));

                // The engine's state changes come from its own threads and are posted, never run on them.
                devices.SetMic(false);
                for (int i = 0; i < 200 && c.State != EngineState.MicLost; i++) Thread.Sleep(10);
                ui.Pump();
                Assert.Equal(EngineState.MicLost, c.State);
            }
            Assert.True(File.Exists(Path.Combine(data, "settings.json")));
        }
        finally
        {
            TestFolders.Delete(data);
        }
    }

    [Fact]
    public void EveryStart_BeginsOnTheNormalVoice_AndKeepsTheRest()
    {
        var data = TestFolders.New("EV_UiNormalStart_");
        PlatformServices Platform() => TestPlatform.Create(new TestAudioDevices(cablePresent: true), new FakeUpdaterPlatform(), SoundDecoders.Default);
        var ui = new ManualDispatcher();
        try
        {
            using (var c = new AppController(data, Platform(), ui))
            {
                c.Start();
                c.SelectVoice("female");
                c.MicMuted = true;
                c.Settings.Voices.First(v => v.Id == "robot").Hotkey = "Ctrl+5";
                Assert.Equal("female", c.ActiveVoice.Id);
                SoundsSettled(c, ui);
            } // quitting saves
            Assert.Contains("\"ActiveVoiceId\": \"female\"", File.ReadAllText(Path.Combine(data, "settings.json")));

            using (var c = new AppController(data, Platform(), ui))
            {
                Assert.Equal(AppController.NormalVoiceId, c.ActiveVoice.Id); // before Start: the window and the tray are made first
                c.Start();
                Assert.Equal(AppController.NormalVoiceId, c.ActiveVoice.Id);
                Assert.Equal(AppController.NormalVoiceId, c.Settings.ActiveVoiceId);
                Assert.True(c.MicMuted);
                Assert.True(c.FilterEnabled);
                Assert.Equal("Ctrl+5", c.Settings.Voices.First(v => v.Id == "robot").Hotkey);
                SoundsSettled(c, ui);
            }
        }
        finally
        {
            TestFolders.Delete(data);
        }
    }

    /// <summary>The sounds load (and the starter ones are written) in the background: done before the folder goes.</summary>
    private static void SoundsSettled(AppController c, ManualDispatcher ui)
    {
        for (int i = 0; i < 500 && !c.Settings.Sounds.All(s => c.IsSoundLoaded(s.Id) || c.IsSoundBroken(s.Id)); i++)
        {
            Thread.Sleep(10);
            ui.Pump();
        }
    }

    [Fact]
    public void KeyMap_GivesTheSavedVirtualKeys()
    {
        Assert.Equal('7', KeyMap.VirtualKey(Avalonia.Input.Key.D7));
        Assert.Equal('Q', KeyMap.VirtualKey(Avalonia.Input.Key.Q));
        Assert.Equal(0x78, KeyMap.VirtualKey(Avalonia.Input.Key.F9));
        Assert.Equal(0x61, KeyMap.VirtualKey(Avalonia.Input.Key.NumPad1));
        Assert.Equal(0xBA, KeyMap.VirtualKey(Avalonia.Input.Key.OemSemicolon));
        Assert.Equal(0, KeyMap.VirtualKey(Avalonia.Input.Key.Enter));
        Assert.Equal(HotkeyMods.Ctrl | HotkeyMods.Shift,
            KeyMap.Mods(Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift));
        // Every key a hotkey can be saved with comes back from the key that types it.
        foreach (var name in new[] { "A", "Z", "0", "9", "F1", "F24", "NumPad0", "Space", "PageUp", "Oem7", "Pause" })
        {
            Assert.True(Hotkey.TryParse(name, out var key), name);
            Assert.Contains(Enum.GetValues<Avalonia.Input.Key>(), k => KeyMap.VirtualKey(k) == key.VirtualKey);
        }
    }

    /// <summary>
    /// A virtual mic EasyVocal makes itself (like Linux'): the first creation finishes when the test says so, later ones
    /// at once; <see cref="Present"/> is whether its device is in the list; a removal can be made to hang.
    /// </summary>
    private sealed class GatedVirtualMic : IVirtualMicProvider
    {
        public readonly TaskCompletionSource<bool> Created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource RemoveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Present, LaterCreatesWork = true;
        public int Creates, Removes;
        public string Name => "Test mic";
        public string CallAppDeviceName => "Test mic";
        public VirtualMicTexts Texts { get; } = new("", "", "", "", "", "", "");
        public string PageUrl => "https://example.com";
        public CableNames Cable => CableNames.EasyVocalLinux;
        public DeviceInfo? Find(IReadOnlyList<DeviceInfo> outputs) => Present ? new DeviceInfo("easyvocal_out", "EasyVocal-Output", false) : null;
        public bool CanInstall => false;
        public bool TakesDefaultsOnInstall => false;
        public Task<InstallResult> InstallAsync(IProgress<double> progress, CancellationToken ct) => Task.FromResult(new InstallResult(InstallStatus.Failed));
        public bool CanCreate => true;

        public async Task<bool> CreateAsync(CancellationToken ct)
        {
            bool made = Interlocked.Increment(ref Creates) == 1 ? await Created.Task : LaterCreatesWork;
            if (made) Present = true;
            return made;
        }

        public Task RemoveAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Removes);
            Present = false;
            return RemoveGate.Task;
        }

        public void OpenPage() { }
    }

    private static PlatformServices WithMic(IVirtualMicProvider mic) =>
        TestPlatform.Create(new TestAudioDevices(cablePresent: true), new FakeUpdaterPlatform(), SoundDecoders.Default) with { VirtualMic = mic };

    private static void Until(Func<bool> condition, ManualDispatcher ui)
    {
        for (int i = 0; i < 300 && !condition(); i++)
        {
            Thread.Sleep(10);
            ui.Pump();
        }
        Assert.True(condition(), "the condition never came true");
        Thread.Sleep(50); // what the finished work posted (its continuation) is run too
        ui.Pump();
    }

    [Fact]
    public void VirtualMic_IsMadeOffTheUiThread_AndRemovedOnQuit()
    {
        var data = TestFolders.New("EV_UiController_");
        var ui = new ManualDispatcher();
        var mic = new GatedVirtualMic();
        mic.RemoveGate.SetResult();
        try
        {
            using (var c = new AppController(data, WithMic(mic), ui))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                c.Start(); // the creation hangs: Start must not wait for it
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "Start took " + watch.Elapsed);
                Assert.True(c.PreparingVirtualMic);
                mic.Created.SetResult(true);
                Until(() => !c.PreparingVirtualMic, ui);
                ui.TickAll(); // the recovery right after: it is there, nothing to make again
                Assert.Equal(1, mic.Creates);
                Assert.Equal(0, mic.Removes);
            }
            // Asked to remove it (on the thread pool: a busy machine may take a moment to get there).
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref mic.Removes) == 1, 5000), "the removal was asked for");
        }
        finally { TestFolders.Delete(data); }
    }

    [Fact]
    public void VirtualMic_StillBeingMadeWhenEasyVocalQuits_IsRemovedOnceItIsThere()
    {
        var data = TestFolders.New("EV_UiController_");
        var ui = new ManualDispatcher();
        var mic = new GatedVirtualMic();
        mic.RemoveGate.SetResult();
        try
        {
            var c = new AppController(data, WithMic(mic), ui);
            c.Start();
            Assert.True(c.PreparingVirtualMic);
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                mic.Created.SetResult(true); // finishes while Dispose is waiting for it
            });
            c.Dispose();
            // Asked to remove it (on the thread pool: a busy machine may take a moment to get there).
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref mic.Removes) == 1, 5000), "the removal was asked for");
        }
        finally { TestFolders.Delete(data); }
    }

    [Fact]
    public void VirtualMic_ThatCouldNotBeMade_IsStillAskedToRemoveWhatIsLeft()
    {
        var data = TestFolders.New("EV_UiController_");
        var ui = new ManualDispatcher();
        var mic = new GatedVirtualMic();
        mic.Created.SetResult(false);
        mic.RemoveGate.SetResult();
        try
        {
            using (var c = new AppController(data, WithMic(mic), ui)) c.Start();
            Assert.Equal(1, mic.Creates);
            // Asked to remove it (on the thread pool: a busy machine may take a moment to get there).
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref mic.Removes) == 1, 5000), "the removal was asked for"); // a half-made one is found by name: the platform decides what is there
        }
        finally { TestFolders.Delete(data); }
    }

    [Fact]
    public void VirtualMic_ThatWentMissing_IsMadeAgain_LessOftenAfterFailures()
    {
        var data = TestFolders.New("EV_UiController_");
        var ui = new ManualDispatcher();
        var mic = new GatedVirtualMic();
        mic.RemoveGate.SetResult();
        try
        {
            using var c = new AppController(data, WithMic(mic), ui) { VirtualMicRetry = TimeSpan.FromMilliseconds(200) };
            c.Start();
            mic.Created.SetResult(true);
            Until(() => !c.PreparingVirtualMic, ui);
            Assert.Equal(1, mic.Creates);

            // The sound server restarted and took it along: the recovery makes it again (not before the retry time).
            mic.Present = false;
            ui.TickAll();
            Assert.Equal(1, mic.Creates);
            Thread.Sleep(300);
            ui.TickAll();
            Until(() => !c.PreparingVirtualMic && mic.Creates == 2, ui);
            Assert.True(mic.Present);

            // Failing: the next try waits twice as long, then four times...
            mic.LaterCreatesWork = false;
            mic.Present = false;
            Thread.Sleep(300);
            ui.TickAll();
            Until(() => !c.PreparingVirtualMic && mic.Creates == 3, ui);
            Thread.Sleep(200);
            ui.TickAll();
            Assert.Equal(3, mic.Creates); // well within twice the wait after a failure: not yet
            // ...and then it tries again (the recovery timer keeps ticking; a busy computer may be late to it).
            for (int i = 0; i < 300 && mic.Creates < 4; i++)
            {
                Thread.Sleep(10);
                ui.TickAll();
                ui.Pump();
            }
            Until(() => !c.PreparingVirtualMic && mic.Creates == 4, ui);
        }
        finally { TestFolders.Delete(data); }
    }

    [Fact]
    public void Quitting_SavesFirst_AndWaitsForAHangingRemovalOnlyBriefly()
    {
        var data = TestFolders.New("EV_UiController_");
        var ui = new ManualDispatcher();
        var mic = new GatedVirtualMic(); // its removal never finishes
        try
        {
            var c = new AppController(data, WithMic(mic), ui);
            c.Start();
            mic.Created.SetResult(true);
            Until(() => !c.PreparingVirtualMic, ui);
            c.MicMuted = true; // a change still waiting for the save timer
            var settings = Path.Combine(data, "settings.json");
            File.Delete(settings);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            c.Dispose();
            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "quitting took " + watch.Elapsed);
            // Asked to remove it (on the thread pool: a busy machine may take a moment to get there).
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref mic.Removes) == 1, 5000), "the removal was asked for");
            Assert.Contains("\"MicMuted\": true", File.ReadAllText(settings));
        }
        finally
        {
            mic.RemoveGate.TrySetResult();
            TestFolders.Delete(data);
        }
    }

    /// <summary>A virtual mic that records every call that would touch the system.</summary>
    private sealed class RecordingVirtualMic : IVirtualMicProvider
    {
        public int Touches;
        public string Name => "EasyVocal-Microphone";
        public string CallAppDeviceName => "EasyVocal-Microphone";
        public VirtualMicTexts Texts { get; } = new("explained", "", "", "", "", "", "");
        public string PageUrl => "https://example.com";
        public CableNames Cable => CableNames.EasyVocalLinux;
        public DeviceInfo? Find(IReadOnlyList<DeviceInfo> outputs) => DeviceRules.FindVirtualCable(Cable, outputs);
        public bool CanInstall => true;
        public bool TakesDefaultsOnInstall => false;
        public Task<InstallResult> InstallAsync(IProgress<double> progress, CancellationToken ct) { Interlocked.Increment(ref Touches); return Task.FromResult(new InstallResult(InstallStatus.Installed)); }
        public bool CanCreate => true;
        public Task<bool> CreateAsync(CancellationToken ct) { Interlocked.Increment(ref Touches); return Task.FromResult(true); }
        public Task RemoveAsync(CancellationToken ct) { Interlocked.Increment(ref Touches); return Task.CompletedTask; }
        public void OpenPage() => Interlocked.Increment(ref Touches);
    }

    [Fact]
    public async Task TestRuns_NeverTouchTheRealVirtualMic_NorTheSystemDefaults()
    {
        var real = new RecordingVirtualMic();
        var devices = new TestAudioDevices(cablePresent: true);
        var computer = WithMic(real);
        var startup = new MemoryStartup();

        var smoke = PlatformFactory.ForTests(computer, null, startup);
        Assert.Same(startup, smoke.Startup);
        Assert.False(smoke.VirtualMic.CanCreate);
        Assert.Equal("explained", smoke.VirtualMic.Texts.Explanation); // the real one's names and texts
        Assert.Equal(CableNames.EasyVocalLinux, smoke.VirtualMic.Cable);
        Assert.False(await smoke.VirtualMic.CreateAsync(default));
        await smoke.VirtualMic.RemoveAsync(default);
        Assert.Equal(InstallStatus.Failed, (await smoke.VirtualMic.InstallAsync(new Progress<double>(), default)).Status);
        smoke.VirtualMic.OpenPage();
        Assert.False(smoke.Audio.SetDefault(TestAudioDevices.HeadphonesId));

        var data = TestFolders.New("EV_UiController_");
        try
        {
            using (var c = new AppController(data, smoke, new ManualDispatcher())) c.Start();
            Assert.Equal(0, real.Touches);
        }
        finally { TestFolders.Delete(data); }

        // The e2e test: its own devices and the VB-CABLE look-alike they are named after.
        var e2e = PlatformFactory.ForTests(computer, devices, startup);
        Assert.Same(devices, e2e.Audio);
        Assert.IsType<TestVirtualMic>(e2e.VirtualMic);
        Assert.Equal(0, real.Touches);
    }
}
