using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Sounds;
using EasyVocal.E2E;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.UiTests;

/// <summary>
/// AI voices in the controller with a stand-in engine: a card per installed voice, picking one (also by hotkey) loads
/// it off the UI thread and the chain plays it, a classic voice / the filter editor's preview takes it out, a slow
/// load that was overtaken is thrown away, failures and a missing pack are reported, never thrown.
/// </summary>
[Collection("App sessions")]
public class AiControllerTests
{
    private sealed class FakeConverter : IVoiceConverter
    {
        public int Processed;
        public bool Disposed;
        public string Voice = "";
        public int LatencySamples => 9600;
        public void Process(Span<float> mono) { if (!Disposed) Interlocked.Increment(ref Processed); }
        public void Reset() { }
        public IVoiceConverter CreateFollower() => new FakeConverter { Voice = Voice };
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeAi : IVoiceConverterSource
    {
        public bool Available { get; set; } = true;
        public string? UnavailableReason => Available ? null : "The AI pack isn't installed.";
        public readonly List<(string Id, string? Quality, TaskCompletionSource<IVoiceConverter> Load)> Loads = new();

        public Task<IVoiceConverter> CreateAsync(string aiVoiceId, string? quality, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<IVoiceConverter>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Loads) Loads.Add((aiVoiceId, quality, tcs));
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

        public FakeConverter Finish(int index)
        {
            var c = new FakeConverter { Voice = Loads[index].Id };
            Loads[index].Load.SetResult(c);
            return c;
        }
    }

    private static PlatformServices Platform() =>
        TestPlatform.Create(new TestAudioDevices(cablePresent: true), new FakeUpdaterPlatform(), SoundDecoders.Default);

    /// <summary>Lets the load's continuation post to the UI thread, then runs it.</summary>
    private static void Settle(ControllerTests.ManualDispatcher ui, Func<bool> until)
    {
        for (int i = 0; i < 200 && !until(); i++) { Thread.Sleep(5); ui.Pump(); }
        ui.Pump();
    }

    [Fact]
    public void Picking_an_AI_voice_loads_it_and_the_chain_plays_it()
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
            int classicLatency = c.VoiceLatencyMs;

            c.SelectVoice("ai-one");
            Assert.Equal(AiVoiceState.Loading, c.AiState);
            Assert.Single(ai.Loads);
            var one = ai.Finish(0);
            Settle(ui, () => c.AiState == AiVoiceState.Ready);
            Assert.Equal(AiVoiceState.Ready, c.AiState);
            Assert.Equal(classicLatency + 200, c.VoiceLatencyMs);
            for (int i = 0; i < 100 && one.Processed == 0; i++) Thread.Sleep(10); // the test mic plays in real time
            Assert.True(one.Processed > 0, "the mic goes through the AI voice");

            // a classic voice takes it out
            c.SelectVoice("robot");
            Assert.Equal(AiVoiceState.Off, c.AiState);
            Assert.True(one.Disposed);

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
            Assert.True(late.Disposed, "the overtaken load is disposed");
            Assert.False(two.Disposed);
            Assert.Equal(AiVoiceState.Ready, c.AiState);

            // the filter editor's preview plays classic sliders only; closing it brings the AI voice back
            c.PreviewSliders(new VoiceSliders { Echo = 0.5 });
            Assert.Equal(AiVoiceState.Off, c.AiState);
            Assert.True(two.Disposed);
            c.PreviewSliders(null);
            Assert.Equal(AiVoiceState.Loading, c.AiState);
            Assert.Equal("ai-two", ai.Loads[^1].Id);
        }
        finally { TestFolders.Delete(data); }
    }

    [Fact]
    public void A_failed_load_or_a_missing_pack_is_reported_not_thrown()
    {
        var data = TestFolders.New("EV_UiAi_");
        var ui = new ControllerTests.ManualDispatcher();
        var ai = new FakeAi();
        try
        {
            using var c = new AppController(data, Platform(), ui, _ => ai);
            c.Start();
            c.SelectVoice("ai-one");
            ai.Loads[0].Load.SetException(new NotSupportedException("This voice's model type isn't supported yet."));
            Settle(ui, () => c.AiState == AiVoiceState.Failed);
            Assert.Equal(AiVoiceState.Failed, c.AiState);
            Assert.Contains("isn't supported", c.AiProblem);
            Assert.Equal("ai-one", c.ActiveVoice.Id); // the card stays picked (its classic sliders play)

            c.SelectVoice(AppController.NormalVoiceId);
            Assert.Equal(AiVoiceState.Off, c.AiState);
            Assert.Null(c.AiProblem);
            ai.Available = false;
            c.SelectVoice("ai-two");
            Assert.Equal(AiVoiceState.Unavailable, c.AiState);
            Assert.Contains("AI pack", c.AiProblem);
            Assert.Single(ai.Loads);
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
