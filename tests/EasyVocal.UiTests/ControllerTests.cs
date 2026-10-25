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
}
