using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Sounds;
using EasyVocal.E2E;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.UiTests;

/// <summary>
/// AI voices in the controller with a stand-in engine: a card per installed voice; picking one (also by hotkey) loads it
/// off the UI thread while the previous voice keeps playing, warms it up in the chain and swaps it in once it gives
/// converted audio; a classic voice / the filter editor's preview takes it out (disposed after the crossfade); a load
/// that was overtaken is thrown away; a failed load gives the card back to the voice that plays (banner); a worker that
/// fails while playing is told and tried again once on the processor; never thrown.
/// </summary>
[Collection("App sessions")]
public class AiControllerTests
{
    private sealed class FakeConverter : IVoiceConverter
    {
        public int Processed;
        public volatile bool Disposed;
        public string Voice = "";
        public volatile bool IsReady = true;
        public volatile string? Failure;
        public int LatencySamples => 9600;
        public bool Ready => IsReady;
        public string? Error => Failure;
        public void Process(Span<float> mono) { if (!Disposed) Interlocked.Increment(ref Processed); }
        public void Reset() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeAi : IVoiceConverterSource, IDisposable
    {
        public readonly List<FakeConverter> StandIns = new();
        public Action? OnDispose;

        public IVoiceConverter StandIn(string aiVoiceId)
        {
            var s = new FakeConverter { Voice = "stand-in:" + aiVoiceId };
            StandIns.Add(s);
            return s;
        }

        public void Dispose() => OnDispose?.Invoke();

        public bool Available { get; set; } = true;
        public string? UnavailableReason => Available ? null : "The AI pack isn't installed.";
        public readonly List<(string Id, string? Quality, bool CpuOnly, TaskCompletionSource<IVoiceConverter> Load)> Loads = new();

        public Task<IVoiceConverter> CreateAsync(string aiVoiceId, string? quality, bool cpuOnly, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<IVoiceConverter>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Loads) Loads.Add((aiVoiceId, quality, cpuOnly, tcs));
            return tcs.Task;
        }

        public bool SyncPresets(AppSettings s)
        {
            bool changed = false;
            foreach (var id in new[] { "ai-one", "ai-two" })
                if (!s.Voices.Any(v => v.Id == id))
                {
                    s.Voices.Add(new VoicePreset { Id = id, Name = id, Kind = VoiceKind.Ai, AiVoiceId = id, BuiltIn = true });
                    changed = true;
                }
            return changed;
        }

        public FakeConverter Finish(int index, bool ready = true)
        {
            var c = new FakeConverter { Voice = Loads[index].Id, IsReady = ready };
            Loads[index].Load.SetResult(c);
            return c;
        }
    }

    private static PlatformServices Platform() =>
        TestPlatform.Create(new TestAudioDevices(cablePresent: true), new FakeUpdaterPlatform(), SoundDecoders.Default);

    /// <summary>Lets the load's continuation post to the UI thread, then runs it (and the controller's timers).</summary>
    private static void Settle(ControllerTests.ManualDispatcher ui, Func<bool> until)
    {
        for (int i = 0; i < 300 && !until(); i++) { Thread.Sleep(5); ui.Pump(); ui.TickAll(); }
        ui.Pump();
    }

    private static bool Soon(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++) Thread.Sleep(10);
        return done();
    }

    [Fact]
    public void Picking_an_AI_voice_keeps_the_previous_voice_until_it_plays()
    {
        var data = TestFolders.New("EV_UiAi_");
        var ui = new ControllerTests.ManualDispatcher();
        var ai = new FakeAi();
        try
        {
            using var c = new AppController(data, Platform(), ui, _ => ai);
            c.Start();
            Assert.True(c.AiAvailable);
            Assert.Contains(c.Settings.Voices, v => v.Id == "ai-one" && v.Kind == VoiceKind.Ai);
            c.SelectVoice("robot");

            c.SelectVoice("ai-one");
            Assert.Equal(AiVoiceState.Loading, c.AiState);
            Assert.Equal("ai-one", c.AiLoadingVoiceId);
            Assert.Equal("robot", c.PlayingVoiceId); // the previous voice plays while it loads (never the clean voice)
            Assert.Single(ai.Loads);

            // loaded but not converting yet: warmed up in the chain, still not heard
            var one = ai.Finish(0, ready: false);
            Settle(ui, () => one.Processed > 0);
            Assert.True(one.Processed > 0, "the loaded voice is fed the mic (warming up)");
            Assert.Equal(AiVoiceState.Loading, c.AiState);
            Assert.Equal("robot", c.PlayingVoiceId);
            one.IsReady = true;
            Settle(ui, () => c.AiState == AiVoiceState.Ready);
            Assert.Equal(AiVoiceState.Ready, c.AiState);
            Assert.Equal("ai-one", c.PlayingVoiceId);
            Assert.Null(c.AiLoadingVoiceId);
            Assert.Equal(200, c.VoiceLatencyMs); // its 9600 samples, the AI card's sliders add nothing

            // a classic voice takes it out (disposed once the crossfade is over)
            c.SelectVoice("robot");
            Assert.Equal(AiVoiceState.Off, c.AiState);
            Assert.True(Soon(() => one.Disposed), "the AI voice is disposed after the crossfade");

            // by hotkey, and a slow load overtaken by another is thrown away
            c.Settings.Voices.First(v => v.Id == "ai-two").Hotkey = "Ctrl+6";
            c.RebuildHotkeys();
            c.SelectVoice("ai-one");
            Assert.True(c.SimulateHotkeyPress("voice:ai-two"));
            Assert.Equal("ai-two", c.ActiveVoice.Id);
            Assert.Equal(3, ai.Loads.Count);
            var two = ai.Finish(2);
            Settle(ui, () => c.AiState == AiVoiceState.Ready);
            var late = ai.Finish(1);
            Settle(ui, () => late.Disposed);
            Assert.True(Soon(() => late.Disposed), "the overtaken load is disposed");
            Assert.False(two.Disposed);
            Assert.Equal(AiVoiceState.Ready, c.AiState);

            // the quality setting reloads it while it keeps playing
            c.SetAiQuality("light");
            Assert.Equal(AiVoiceState.Loading, c.AiState);
            Assert.Equal("ai-two", c.PlayingVoiceId);
            var twoLight = ai.Finish(3);
            Settle(ui, () => c.AiState == AiVoiceState.Ready);
            Assert.True(Soon(() => two.Disposed), "the previous level is let go once the new one plays");
            Assert.False(twoLight.Disposed);

            // the filter editor's preview keeps the AI voice (its sliders play after it); closing it reloads nothing
            int loads = ai.Loads.Count;
            c.PreviewSliders(new VoiceSliders { Echo = 0.5 });
            Assert.Equal(AiVoiceState.Ready, c.AiState);
            Assert.Equal("ai-two", c.PlayingVoiceId);
            int fed = Volatile.Read(ref twoLight.Processed);
            Settle(ui, () => Volatile.Read(ref twoLight.Processed) > fed + 5);
            Assert.True(Volatile.Read(ref twoLight.Processed) > fed + 5, "the AI voice keeps converting during the preview");
            c.PreviewSliders(null);
            Assert.Equal(AiVoiceState.Ready, c.AiState);
            Assert.Equal(loads, ai.Loads.Count);
            Thread.Sleep(300);
            Assert.False(twoLight.Disposed);
            Assert.Empty(ai.StandIns); // a voice that wasn't clean played meanwhile every time
        }
        finally { TestFolders.Delete(data); }
    }

    [Fact]
    public void From_a_clean_voice_an_AI_voice_plays_its_stand_in_until_it_is_ready()
    {
        var data = TestFolders.New("EV_UiAi_");
        var ui = new ControllerTests.ManualDispatcher();
        var ai = new FakeAi();
        try
        {
            using var c = new AppController(data, Platform(), ui, _ => ai);
            c.Start();
            Assert.Equal(AppController.NormalVoiceId, c.PlayingVoiceId); // every start: Normal, the clean voice
            c.SelectVoice("ai-one");
            Assert.Equal(AiVoiceState.Loading, c.AiState);
            var first = Assert.Single(ai.StandIns);
            Assert.Equal("stand-in:ai-one", first.Voice);
            Settle(ui, () => first.Processed > 0);
            Assert.True(first.Processed > 0, "the stand-in voice plays while it loads, never the clean voice");

            // another AI voice picked meanwhile: its own stand-in takes over
            c.SelectVoice("ai-two");
            Assert.Equal(2, ai.StandIns.Count);
            Assert.True(Soon(() => first.Disposed), "the first stand-in is let go once the chain faded it out");
            var two = ai.Finish(1, ready: false);
            Settle(ui, () => two.Processed > 0);
            Assert.False(ai.StandIns[1].Disposed, "still heard while the AI voice warms up");
            two.IsReady = true;
            Settle(ui, () => c.AiState == AiVoiceState.Ready);
            Assert.Equal("ai-two", c.PlayingVoiceId);
            Assert.True(Soon(() => ai.StandIns[1].Disposed), "the AI voice took over");
            Assert.False(two.Disposed);

            // a load that fails: the card goes back to Normal and its stand-in goes too
            c.SelectVoice(AppController.NormalVoiceId);
            c.SelectVoice("ai-one");
            Assert.Equal(3, ai.StandIns.Count);
            ai.Loads[2].Load.SetException(new InvalidDataException("damaged"));
            Settle(ui, () => c.AiProblem != null);
            Assert.Equal(AppController.NormalVoiceId, c.ActiveVoice.Id);
            Assert.True(Soon(() => ai.StandIns[2].Disposed));
        }
        finally { TestFolders.Delete(data); }
    }

    [Fact]
    public void Quitting_saves_first_then_stops_every_AI_voice_then_closes_the_AI_engine()
    {
        var data = TestFolders.New("EV_UiAi_");
        var ui = new ControllerTests.ManualDispatcher();
        var ai = new FakeAi();
        try
        {
            var c = new AppController(data, Platform(), ui, _ => ai);
            c.Start();
            c.SelectVoice("ai-one");
            var one = ai.Finish(0);
            Settle(ui, () => c.AiState == AiVoiceState.Ready);
            c.SelectVoice("ai-two");
            var two = ai.Finish(1, ready: false);
            Settle(ui, () => two.Processed > 0); // warming up while ai-one plays
            c.MicMuted = true;                    // saved half a second later: not yet
            bool saved = false, stopped = false;
            ai.OnDispose = () =>
            {
                saved = File.ReadAllText(Path.Combine(data, "settings.json")).Contains("\"MicMuted\": true");
                stopped = one.Disposed && two.Disposed && ai.StandIns.All(s => s.Disposed);
            };
            c.Dispose();
            Assert.True(saved, "the settings are saved before anything that may take a while");
            Assert.True(stopped, "every AI voice is stopped before the AI engine closes its models");
        }
        finally { TestFolders.Delete(data); }
    }

    [Fact]
    public void The_AI_cards_are_shown_even_when_the_hotkeys_are_not_rebuilt()
    {
        var data = TestFolders.New("EV_UiAi_");
        var ui = new ControllerTests.ManualDispatcher();
        var ai = new FakeAi();
        try
        {
            using var c = new AppController(data, Platform(), ui, _ => ai);
            c.Start();
            c.SetTyping(true); // typing in a box: RebuildHotkeys leaves the hotkeys alone (and says nothing)
            int changes = 0;
            c.Changed += () => changes++;
            c.AiVoicesChanged();
            Assert.Equal(1, changes);
            c.AddAiVoice(new EasyVocal.Ai.Store.VoiceManifest { Id = "user-mine", Name = "Mine", Emoji = "🧠", Kind = EasyVocal.Ai.Store.VoiceKinds.Pth });
            Assert.Equal(2, changes);
            Assert.Contains(c.Settings.Voices, v => v.Id == "user-mine");
        }
        finally { TestFolders.Delete(data); }
    }

    [Fact]
    public void A_failed_load_or_a_missing_pack_gives_the_card_back_and_is_reported()
    {
        var data = TestFolders.New("EV_UiAi_");
        var ui = new ControllerTests.ManualDispatcher();
        var ai = new FakeAi();
        try
        {
            using var c = new AppController(data, Platform(), ui, _ => ai);
            c.Start();
            c.SelectVoice("robot");
            c.SelectVoice("ai-one");
            ai.Loads[0].Load.SetException(new NotSupportedException("This voice's model type isn't supported yet."));
            Settle(ui, () => c.AiProblem != null);
            Assert.Contains("isn't supported", c.AiProblem);
            Assert.Equal("robot", c.ActiveVoice.Id); // the voice that plays keeps the card
            Assert.Equal("robot", c.PlayingVoiceId);
            Assert.Equal(AiVoiceState.Off, c.AiState);
            c.DismissAiProblem();
            Assert.Null(c.AiProblem);

            ai.Available = false;
            c.SelectVoice("ai-two");
            Assert.Contains("AI pack", c.AiProblem);
            Assert.Equal("robot", c.ActiveVoice.Id);
            Assert.Single(ai.Loads);
        }
        finally { TestFolders.Delete(data); }
    }

    [Fact]
    public void A_worker_that_fails_while_playing_is_told_and_tried_again_once_on_the_processor()
    {
        var data = TestFolders.New("EV_UiAi_");
        var ui = new ControllerTests.ManualDispatcher();
        var ai = new FakeAi();
        try
        {
            using var c = new AppController(data, Platform(), ui, _ => ai);
            c.Start();
            c.SelectVoice("ai-one");
            var gpu = ai.Finish(0);
            Settle(ui, () => c.AiState == AiVoiceState.Ready);
            Assert.False(ai.Loads[0].CpuOnly);

            gpu.Failure = "the graphics card stopped";
            Settle(ui, () => ai.Loads.Count == 2);
            Assert.Equal(2, ai.Loads.Count);
            Assert.True(ai.Loads[1].CpuOnly, "the retry runs on the processor");
            Assert.Contains("graphics card stopped", c.AiProblem);
            Assert.Equal("ai-one", c.PlayingVoiceId); // the failed one keeps playing its stand-in voice meanwhile
            var cpu = ai.Finish(1);
            Settle(ui, () => c.AiState == AiVoiceState.Ready && c.PlayingVoiceId == "ai-one");
            Assert.True(Soon(() => gpu.Disposed));

            cpu.Failure = "out of memory";
            Settle(ui, () => c.AiState == AiVoiceState.Failed);
            Assert.Equal(AiVoiceState.Failed, c.AiState);
            Assert.Equal(2, ai.Loads.Count); // tried once only
            Assert.Contains("out of memory", c.AiProblem);
            Assert.False(cpu.Disposed, "it stays in the chain (its stand-in voice, never the person's own)");
        }
        finally { TestFolders.Delete(data); }
    }

    [Fact]
    public void Without_an_AI_engine_nothing_changes()
    {
        var data = TestFolders.New("EV_UiAi_");
        try
        {
            using var c = new AppController(data, Platform(), new ControllerTests.ManualDispatcher());
            c.Start();
            Assert.False(c.AiAvailable);
            Assert.Null(c.AiUnavailableReason);
            Assert.DoesNotContain(c.Settings.Voices, v => v.Kind == VoiceKind.Ai);
            c.SelectVoice("robot");
            Assert.Equal(AiVoiceState.Off, c.AiState);
        }
        finally { TestFolders.Delete(data); }
    }
}
