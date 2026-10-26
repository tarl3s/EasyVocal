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
        public IVoiceConverter CreateFollower() => new FakeConverter { Voice = Voice };
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeAi : IVoiceConverterSource
    {
        public bool Available { get; set; } = true;
        public string? UnavailableReason => Available ? null : "The AI pack isn't installed.";
        public readonly List<(string Id, string? Quality, bool CpuOnly, TaskCompletionSource<IVoiceConverter> Load)> Loads = new();

        public Task<IVoiceConverter> CreateAsync(string aiVoiceId, string? quality, CancellationToken ct) => CreateAsync(aiVoiceId, quality, false, ct);

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

            // the filter editor's preview plays classic sliders only; closing it brings the AI voice back
            c.PreviewSliders(new VoiceSliders { Echo = 0.5 });
            Assert.Equal(AiVoiceState.Off, c.AiState);
            Assert.True(Soon(() => twoLight.Disposed));
            c.PreviewSliders(null);
            Assert.Equal(AiVoiceState.Loading, c.AiState);
            Assert.Equal("ai-two", ai.Loads[^1].Id);
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
