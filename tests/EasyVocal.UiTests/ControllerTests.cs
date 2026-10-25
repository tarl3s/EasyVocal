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
        var data = Path.Combine(Path.GetTempPath(), "EV_UiController_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
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
            for (int attempt = 0; attempt < 10 && Directory.Exists(data); attempt++)
            {
                try { Directory.Delete(data, recursive: true); }
                catch (IOException) { Thread.Sleep(200); }
            }
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

    /// <summary>A virtual mic EasyVocal makes itself (like Linux'), whose creation finishes when the test says so.</summary>
    private sealed class GatedVirtualMic : IVirtualMicProvider
    {
        public readonly TaskCompletionSource<bool> Created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Creates, Removes, Cleans;
        public string Name => "Test mic";
        public string CallAppDeviceName => "Test mic";
        public VirtualMicTexts Texts { get; } = new("", "", "", "", "", "", "");
        public string PageUrl => "https://example.com";
        public CableNames Cable => CableNames.EasyVocalLinux;
        public DeviceInfo? Find(IReadOnlyList<DeviceInfo> outputs) => null;
        public bool CanInstall => false;
        public bool TakesDefaultsOnInstall => false;
        public Task<InstallResult> InstallAsync(IProgress<double> progress, CancellationToken ct) => Task.FromResult(new InstallResult(InstallStatus.Failed));
        public bool CanCreate => true;
        public Task CleanStaleAsync(CancellationToken ct) { Interlocked.Increment(ref Cleans); return Task.CompletedTask; }
        public Task<bool> CreateAsync(CancellationToken ct) { Interlocked.Increment(ref Creates); return Created.Task; }
        public Task RemoveAsync(CancellationToken ct) { Interlocked.Increment(ref Removes); return Task.CompletedTask; }
        public void OpenPage() { }
    }

    private static string NewData()
    {
        var data = Path.Combine(Path.GetTempPath(), "EV_UiController_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        return data;
    }

    private static void DeleteData(string data)
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(data); attempt++)
        {
            try { Directory.Delete(data, recursive: true); }
            catch (IOException) { Thread.Sleep(200); }
        }
    }

    [Fact]
    public void VirtualMic_IsMadeOffTheUiThread_AndRemovedOnQuit()
    {
        var data = NewData();
        var ui = new ManualDispatcher();
        var mic = new GatedVirtualMic();
        var platform = TestPlatform.Create(new TestAudioDevices(cablePresent: true), new FakeUpdaterPlatform(), SoundDecoders.Default) with { VirtualMic = mic };
        try
        {
            using (var c = new AppController(data, platform, ui))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                c.Start(); // the creation hangs: Start must not wait for it
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "Start took " + watch.Elapsed);
                Assert.True(c.PreparingVirtualMic);
                mic.Created.SetResult(true);
                for (int i = 0; i < 200 && c.PreparingVirtualMic; i++) Thread.Sleep(10);
                ui.Pump();
                Assert.False(c.PreparingVirtualMic);
                Assert.Equal(1, mic.Cleans);
                Assert.Equal(1, mic.Creates);
                Assert.Equal(0, mic.Removes);
            }
            Assert.Equal(1, mic.Removes);
        }
        finally { DeleteData(data); }
    }

    [Fact]
    public void VirtualMic_StillBeingMadeWhenEasyVocalQuits_IsRemovedOnceItIsThere()
    {
        var data = NewData();
        var ui = new ManualDispatcher();
        var mic = new GatedVirtualMic();
        var platform = TestPlatform.Create(new TestAudioDevices(cablePresent: true), new FakeUpdaterPlatform(), SoundDecoders.Default) with { VirtualMic = mic };
        try
        {
            var c = new AppController(data, platform, ui);
            c.Start();
            Assert.True(c.PreparingVirtualMic);
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                mic.Created.SetResult(true); // finishes while Dispose is waiting for it
            });
            c.Dispose();
            Assert.Equal(1, mic.Removes);
        }
        finally { DeleteData(data); }
    }

    [Fact]
    public void VirtualMic_ThatCouldNotBeMade_IsNotRemoved()
    {
        var data = NewData();
        var ui = new ManualDispatcher();
        var mic = new GatedVirtualMic();
        mic.Created.SetResult(false);
        var platform = TestPlatform.Create(new TestAudioDevices(cablePresent: true), new FakeUpdaterPlatform(), SoundDecoders.Default) with { VirtualMic = mic };
        try
        {
            using (var c = new AppController(data, platform, ui)) c.Start();
            Assert.Equal(1, mic.Creates);
            Assert.Equal(0, mic.Removes);
        }
        finally { DeleteData(data); }
    }
}
