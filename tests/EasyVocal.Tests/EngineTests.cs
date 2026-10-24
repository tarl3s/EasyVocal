using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

public class EngineTests
{
    private const int Sr = AudioFormat.SampleRate;

    /// <summary>The limiter after the mix delays everything by this much (R1).</summary>
    private static readonly int Lag = new Limiter(Sr).LatencySamples;

    /// <summary>An engine wired to fakes; every device the factories made is kept for inspection.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly List<FakeInput> Inputs = new();
        public readonly List<FakeOutput> Calls = new();
        public readonly List<FakeOutput> Monitors = new();
        public readonly List<EngineState> Changes = new();
        public readonly VoiceChain Chain = new(Sr);
        public readonly SoundMixer Mixer = new();
        public readonly AudioEngine Engine;
        public Func<FakeOutput>? CallFactory;
        public bool WithMonitor = true;
        public bool MicMissing;

        public Rig()
        {
            Engine = new AudioEngine(
                () =>
                {
                    if (MicMissing) throw new IOException("no mic");
                    var i = new FakeInput();
                    Inputs.Add(i);
                    return i;
                },
                () => { var o = CallFactory?.Invoke() ?? new FakeOutput(); Calls.Add(o); return o; },
                () => { if (!WithMonitor) return null; var o = new FakeOutput(); Monitors.Add(o); return o; },
                Chain, Mixer);
            Engine.StateChanged += s => Changes.Add(s);
        }

        public FakeInput Mic => Inputs[^1];
        public FakeOutput Call => Calls[^1];
        public FakeOutput Monitor => Monitors[^1];

        public void Dispose() => Engine.Dispose();
    }

    private static VoiceSliders Preset(string id) => BuiltInVoices.All().First(v => v.Id == id).Sliders.Clone();

    /// <summary>Asserts <paramref name="output"/> is <paramref name="expected"/> delayed by the limiter.</summary>
    private static void AssertDelayedCopy(float[] expected, float[] output, float tolerance = 1e-5f)
    {
        Assert.Equal(expected.Length, output.Length);
        for (int n = 0; n < Lag; n++) Assert.Equal(0f, output[n], tolerance);
        for (int n = 0; n + Lag < output.Length; n++) Assert.Equal(expected[n], output[n + Lag], tolerance);
    }

    private static float[] Add(params float[][] signals)
    {
        var o = new float[signals[0].Length];
        foreach (var s in signals)
            for (int i = 0; i < o.Length; i++) o[i] += s[i];
        return o;
    }

    [Fact]
    public void Start_AllDevicesStarted_Running()
    {
        using var rig = new Rig();
        Assert.Equal(EngineState.Stopped, rig.Engine.State);
        rig.Engine.Start();
        Assert.Equal(EngineState.Running, rig.Engine.State);
        Assert.Equal(new[] { EngineState.Running }, rig.Changes);
        Assert.Equal(1, rig.Mic.StartCount);
        Assert.Equal(1, rig.Call.StartCount);
        Assert.Equal(1, rig.Monitor.StartCount);
    }

    [Fact]
    public void FilterOff_CallGetsCleanVoice()
    {
        using var rig = new Rig();
        rig.Chain.Apply(Preset("demon"));
        rig.Engine.FilterEnabled = false;
        rig.Engine.Start();
        var voice = Signals.Sine(300, 0.5, 0.5f);
        rig.Mic.Feed(voice);
        AssertDelayedCopy(voice, rig.Call.Written);
    }

    [Fact]
    public void FilterOn_CallGetsTheChangedVoice()
    {
        using var rig = new Rig();
        rig.Chain.Apply(Preset("demon"));
        rig.Engine.Start();
        Assert.True(rig.Engine.FilterEnabled);
        var voice = Signals.Sine(300, 0.5, 0.5f);
        rig.Mic.Feed(voice);
        var call = rig.Call.Written;
        double diff = 0;
        for (int n = 0; n + Lag < call.Length; n++) diff = Math.Max(diff, Math.Abs(call[n + Lag] - voice[n]));
        Assert.True(diff > 0.1, $"the demon voice should change the signal (max diff {diff})");
    }

    [Fact]
    public void MicMuted_SoundsStillReachTheCall()
    {
        using var rig = new Rig();
        var sound = Signals.Sine(660, 1, 0.3f);
        rig.Mixer.Set("beep", sound);
        rig.Engine.MicMuted = true;
        rig.Engine.Start();
        rig.Mixer.Toggle("beep", 1f);
        rig.Mic.Feed(Signals.Noise(0.5, 0.5f, 7));
        AssertDelayedCopy(Signals.Slice(sound, 0, 0.5), rig.Call.Written);
    }

    [Fact]
    public void MicMuted_InputLevelStillShowsTheMic()
    {
        using var rig = new Rig();
        rig.Engine.MicMuted = true;
        rig.Engine.Start();
        Assert.Equal(0f, rig.Engine.InputLevel);
        rig.Mic.Feed(Signals.Sine(300, 0.1, 0.6f));
        Assert.InRange(rig.Engine.InputLevel, 0.55f, 0.6f);
        rig.Mic.Feed(Signals.Silence(0.02));
        Assert.Equal(0f, rig.Engine.InputLevel);
    }

    [Fact]
    public void HearMeOff_HearSoundsOn_MonitorGetsOnlySounds()
    {
        using var rig = new Rig();
        var sound = Signals.Sine(660, 1, 0.3f);
        rig.Mixer.Set("beep", sound);
        rig.Engine.HearMe = false;
        rig.Engine.HearSounds = true;
        rig.Engine.Start();
        rig.Mixer.Toggle("beep", 1f);
        var voice = Signals.Sine(200, 0.5, 0.4f);
        rig.Engine.FilterEnabled = false;
        rig.Mic.Feed(voice);
        AssertDelayedCopy(Signals.Slice(sound, 0, 0.5), rig.Monitor.Written);
        AssertDelayedCopy(Add(voice, Signals.Slice(sound, 0, 0.5)), rig.Call.Written);
    }

    [Fact]
    public void HearMeOn_HearSoundsOff_MonitorGetsOnlyVoice()
    {
        using var rig = new Rig();
        rig.Mixer.Set("beep", Signals.Sine(660, 1, 0.3f));
        rig.Engine.HearMe = true;
        rig.Engine.HearSounds = false;
        rig.Engine.FilterEnabled = false;
        rig.Engine.Start();
        rig.Mixer.Toggle("beep", 1f);
        var voice = Signals.Sine(200, 0.5, 0.4f);
        rig.Mic.Feed(voice);
        AssertDelayedCopy(voice, rig.Monitor.Written);
    }

    [Fact]
    public void MonitorBothOff_NothingWritten()
    {
        using var rig = new Rig();
        rig.Engine.HearMe = false;
        rig.Engine.HearSounds = false;
        rig.Engine.Start();
        rig.Mic.Feed(Signals.Sine(200, 0.2, 0.4f));
        Assert.Equal(0, rig.Monitor.WriteCount);
        Assert.Equal(Sr / 5, rig.Call.Written.Length);
    }

    [Fact]
    public void NoMonitorDevice_CallStillWorks()
    {
        using var rig = new Rig { WithMonitor = false };
        rig.Engine.HearMe = true;
        rig.Engine.Start();
        Assert.Equal(EngineState.Running, rig.Engine.State);
        rig.Mic.Feed(Signals.Sine(200, 0.2, 0.4f));
        Assert.Equal(Sr / 5, rig.Call.Written.Length);
    }

    [Fact]
    public void Engine_LoudMix_NeverExceedsOne()
    {
        using var rig = new Rig();
        rig.Mixer.Set("a", Signals.Sine(330, 1, 0.9f));
        rig.Mixer.Set("b", Signals.Sine(550, 1, 0.9f));
        rig.Mixer.Set("c", Signals.Sine(770, 1, 0.9f));
        rig.Engine.HearMe = true;
        rig.Engine.HearSounds = true;
        rig.Engine.Start();
        rig.Mixer.Toggle("a", 1f);
        rig.Mixer.Toggle("b", 1f);
        rig.Mixer.Toggle("c", 1f);
        rig.Mic.Feed(Signals.Sine(220, 1, 0.9f));
        foreach (var output in new[] { rig.Call.Written, rig.Monitor.Written })
        {
            Assert.Equal(Sr, output.Length);
            Assert.True(Signals.AllFinite(output));
            Assert.True(Signals.Peak(output) <= 0.98 + 1e-3, $"peak {Signals.Peak(output)}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(441)]
    [InlineData(1000)]
    [InlineData(4800)]
    public void AnyCaptureBlockSize_SameCallOutput(int chunk)
    {
        float[] Run(int size)
        {
            using var rig = new Rig();
            rig.Mixer.Set("beep", Signals.Sine(660, 1, 0.3f));
            rig.Chain.Apply(Preset("robot"));
            rig.Engine.Start();
            rig.Mixer.Toggle("beep", 1f);
            rig.Mic.Feed(Signals.PulseVowel(140, new[] { 700.0, 1200.0, 2600.0 }, 0.5), size);
            return rig.Call.Written;
        }
        var reference = Run(480);
        var other = Run(chunk);
        Assert.Equal(reference.Length, other.Length);
        // Everything after the mic works sample by sample, so how the mic chunks its audio must not matter.
        for (int i = 0; i < reference.Length; i++) Assert.Equal(reference[i], other[i], 1e-4f);
    }

    [Fact]
    public void CallOutputBacklog_IsTrimmedToTheTargetFill()
    {
        using var rig = new Rig { CallFactory = () => new FakeOutput { BufferedSamples = 4000 } };
        rig.Engine.Start();
        rig.Mic.Feed(Signals.Silence(0.01));
        Assert.Equal(1, rig.Call.TrimCount);
        Assert.Equal(AudioEngine.TargetBacklog, rig.Call.LastTrimTo); // not emptied: a cushion stays
        Assert.Equal(Sr / 50, AudioEngine.TargetBacklog);
        Assert.Equal(EngineState.Running, rig.Engine.State);
    }

    [Fact]
    public void CallOutputSmallBuffer_IsKept()
    {
        using var rig = new Rig { CallFactory = () => new FakeOutput { BufferedSamples = 960 } };
        rig.Engine.Start();
        rig.Mic.Feed(Signals.Silence(0.1));
        Assert.Equal(0, rig.Call.TrimCount);
    }

    [Fact]
    public void MicLost_ThenRecover()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        var first = rig.Mic;
        first.RaiseStopped(new IOException("unplugged"));
        Assert.Equal(EngineState.MicLost, rig.Engine.State);
        Assert.Equal(EngineState.MicLost, rig.Changes[^1]);

        first.Feed(Signals.Sine(200, 0.05, 0.4f)); // a late callback from the dead device is ignored
        Assert.Empty(rig.Call.Written);

        Assert.True(rig.Engine.TryRecover());
        Assert.Equal(EngineState.Running, rig.Engine.State);
        Assert.Equal(EngineState.Running, rig.Changes[^1]);
        Assert.Equal(2, rig.Inputs.Count);
        Assert.True(first.Disposed);
        Assert.False(first.HasListeners);
        Assert.Equal(1, rig.Mic.StartCount);
        rig.Mic.Feed(Signals.Sine(200, 0.05, 0.4f));
        Assert.Equal(Sr / 20, rig.Call.Written.Length);
    }

    [Fact]
    public void MicStoppedWithoutError_IsAlsoLost()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        rig.Mic.RaiseStopped(null);
        Assert.Equal(EngineState.MicLost, rig.Engine.State);
    }

    [Fact]
    public void MicFactoryThrows_MicLost_RecoverLater()
    {
        using var rig = new Rig();
        bool fail = true;
        var engine = new AudioEngine(
            () => fail ? throw new IOException("no mic") : new FakeInput(),
            () => new FakeOutput(), () => null, rig.Chain, rig.Mixer);
        engine.Start();
        Assert.Equal(EngineState.MicLost, engine.State);
        Assert.False(engine.TryRecover());
        fail = false;
        Assert.True(engine.TryRecover());
        Assert.Equal(EngineState.Running, engine.State);
        engine.Dispose();
    }

    [Fact]
    public void OutputThrows_StateOutputLost_NoCrash()
    {
        using var rig = new Rig { CallFactory = () => new FakeOutput { ThrowOnWrite = true } };
        rig.Engine.Start();
        rig.Mic.Feed(Signals.Sine(200, 0.1, 0.4f)); // must not throw
        Assert.Equal(EngineState.OutputLost, rig.Engine.State);
        Assert.Equal(EngineState.OutputLost, rig.Changes[^1]);
        rig.Mic.Feed(Signals.Sine(200, 0.1, 0.4f));

        rig.CallFactory = null;
        Assert.True(rig.Engine.TryRecover());
        Assert.Equal(EngineState.Running, rig.Engine.State);
        Assert.True(rig.Calls[0].Disposed);
        rig.Mic.Feed(Signals.Sine(200, 0.1, 0.4f));
        Assert.Equal(Sr / 10, rig.Call.Written.Length);
    }

    [Fact]
    public void MonitorThrows_CallKeepsRunning()
    {
        using var rig = new Rig();
        rig.Engine.HearMe = true;
        rig.Engine.FilterEnabled = false;
        rig.Engine.Start();
        rig.Monitor.ThrowOnWrite = true;
        var voice = Signals.Sine(200, 0.2, 0.4f);
        rig.Mic.Feed(voice);
        Assert.Equal(EngineState.Running, rig.Engine.State);
        AssertDelayedCopy(voice, rig.Call.Written);
    }

    [Fact]
    public void Start_WhenNoVirtualCable_StateNoVirtualCable()
    {
        using var rig = new Rig { CallFactory = () => throw new NoVirtualCableException() };
        rig.Engine.Start();
        Assert.Equal(EngineState.NoVirtualCable, rig.Engine.State);
        Assert.Equal(EngineState.NoVirtualCable, rig.Changes[^1]);
        Assert.True(rig.Engine.MonitorOnly);

        Assert.False(rig.Engine.TryRecover()); // still not installed
        Assert.Equal(EngineState.NoVirtualCable, rig.Engine.State);

        rig.CallFactory = null; // the user installed it
        Assert.True(rig.Engine.TryRecover());
        Assert.Equal(EngineState.Running, rig.Engine.State);
        Assert.False(rig.Engine.MonitorOnly);
    }

    [Fact]
    public void NoVirtualCable_MonitorOnlySession_HearMeWorks_ThenCableAttachesWithoutRestartingTheMic()
    {
        using var rig = new Rig { CallFactory = () => throw new NoVirtualCableException() };
        rig.Engine.HearMe = true;
        rig.Engine.FilterEnabled = false;
        rig.Engine.Start();
        Assert.Equal(EngineState.NoVirtualCable, rig.Engine.State);
        Assert.Equal(1, rig.Mic.StartCount);

        var voice = Signals.Sine(200, 0.2, 0.4f);
        rig.Mic.Feed(voice);
        AssertDelayedCopy(voice, rig.Monitor.Written); // the voices can be tried before installing
        Assert.Empty(rig.Calls);
        Assert.InRange(rig.Engine.InputLevel, 0.35f, 0.41f);

        Assert.False(rig.Engine.TryRecover());
        Assert.Single(rig.Inputs); // looking for the cable again must not reopen the mic

        rig.CallFactory = null;
        Assert.True(rig.Engine.TryRecover());
        Assert.Equal(EngineState.Running, rig.Engine.State);
        Assert.Equal(EngineState.Running, rig.Changes[^1]);
        Assert.Single(rig.Inputs);
        Assert.Equal(1, rig.Call.StartCount);
        rig.Mic.Feed(Signals.Sine(200, 0.1, 0.4f));
        Assert.Equal(Sr / 10, rig.Call.Written.Length);
    }

    [Fact]
    public void NoVirtualCable_AndNoMic_ReportsTheMicFirst_ThenMonitorOnly()
    {
        using var rig = new Rig { CallFactory = () => throw new NoVirtualCableException(), MicMissing = true };
        rig.Engine.Start();
        Assert.Equal(EngineState.MicLost, rig.Engine.State);
        Assert.False(rig.Engine.MonitorOnly);
        Assert.False(rig.Engine.TryRecover());

        rig.MicMissing = false; // plugged in: the cable is still missing, the voices can be tried
        Assert.False(rig.Engine.TryRecover());
        Assert.Equal(EngineState.NoVirtualCable, rig.Engine.State);
        Assert.True(rig.Engine.MonitorOnly);
    }

    [Fact]
    public void MicMissing_RecoveryOpensNoPlaybackDevice()
    {
        using var rig = new Rig { MicMissing = true };
        rig.Engine.Start();
        Assert.Equal(EngineState.MicLost, rig.Engine.State);
        for (int i = 0; i < 3; i++) Assert.False(rig.Engine.TryRecover()); // the 2 s recovery timer
        Assert.Empty(rig.Calls);
        Assert.Empty(rig.Monitors);

        rig.MicMissing = false;
        Assert.True(rig.Engine.TryRecover());
        Assert.Single(rig.Calls);
        Assert.Single(rig.Monitors);
        Assert.Equal(1, rig.Call.StartCount);
    }

    [Fact]
    public void CallFactoryFails_AfterTheMicWasMade_TheMicIsClosed()
    {
        using var rig = new Rig { CallFactory = () => throw new InvalidOperationException("busy") };
        rig.Engine.Start();
        Assert.Equal(EngineState.OutputLost, rig.Engine.State);
        Assert.True(rig.Mic.Disposed);
        Assert.Equal(0, rig.Mic.StartCount);
        Assert.Empty(rig.Monitors);
    }

    [Fact]
    public void Running_HeadphonesFail_TryRecoverOpensThemAgainWithoutTouchingTheMicOrTheCall()
    {
        using var rig = new Rig();
        rig.Engine.HearMe = true;
        rig.Engine.FilterEnabled = false;
        rig.Engine.Start();
        Assert.True(rig.Engine.TryRecover()); // nothing to do while everything works
        Assert.Single(rig.Monitors);

        var first = rig.Monitor;
        first.ThrowOnWrite = true; // the stream stopped (no device was added or removed)
        rig.Mic.Feed(Signals.Sine(200, 0.05, 0.4f));
        Assert.True(rig.Engine.MonitorLost);
        Assert.Equal(EngineState.Running, rig.Engine.State);

        Assert.True(rig.Engine.TryRecover());
        Assert.Equal(2, rig.Monitors.Count);
        Assert.True(first.Disposed);
        Assert.False(rig.Engine.MonitorLost);
        Assert.Single(rig.Inputs);
        Assert.Single(rig.Calls);
        Assert.Equal(1, rig.Monitor.StartCount);

        var voice = Signals.Sine(200, 0.2, 0.4f);
        int callBefore = rig.Call.Written.Length;
        rig.Mic.Feed(voice);
        AssertDelayedCopy(voice, rig.Monitor.Written);
        Assert.Equal(callBefore + voice.Length, rig.Call.Written.Length);

        Assert.True(rig.Engine.TryRecover());
        Assert.Equal(2, rig.Monitors.Count); // working headphones are left alone
    }

    [Fact]
    public void LeavingRunning_StopsEverySound()
    {
        using var rig = new Rig();
        rig.Mixer.Set("long", Signals.Sine(660, 2, 0.3f));
        rig.Engine.Start();
        Assert.True(rig.Mixer.Toggle("long", 1f));
        rig.Mic.Feed(Signals.Silence(0.05));
        Assert.True(rig.Mixer.IsPlaying("long"));

        rig.Mic.RaiseStopped(new IOException("unplugged"));
        Assert.Equal(EngineState.MicLost, rig.Engine.State);
        Assert.False(rig.Mixer.IsPlaying("long"));

        Assert.True(rig.Engine.TryRecover());
        rig.Mic.Feed(Signals.Silence(0.1));
        Assert.All(rig.Call.Written, v => Assert.Equal(0f, v)); // the sound did not come back late
    }

    [Fact]
    public void Stop_StopsEverySound()
    {
        using var rig = new Rig();
        rig.Mixer.Set("long", Signals.Sine(660, 2, 0.3f));
        rig.Engine.Start();
        rig.Mixer.Toggle("long", 1f);
        rig.Engine.Stop();
        Assert.False(rig.Mixer.IsPlaying("long"));
    }

    [Fact]
    public void MonitorOnly_HeadphonesFail_TryRecoverOpensThemAgain()
    {
        using var rig = new Rig { CallFactory = () => throw new NoVirtualCableException() };
        rig.Engine.HearMe = true;
        rig.Engine.FilterEnabled = false;
        rig.Engine.Start();
        var first = rig.Monitor;
        first.ThrowOnWrite = true; // headphones unplugged
        rig.Mic.Feed(Signals.Sine(200, 0.05, 0.4f));
        Assert.True(rig.Engine.MonitorLost);

        Assert.False(rig.Engine.TryRecover()); // the cable is still missing…
        Assert.Equal(2, rig.Monitors.Count);    // …but the headphones are opened again
        Assert.True(first.Disposed);
        Assert.False(rig.Engine.MonitorLost);
        Assert.True(rig.Engine.MonitorOnly);
        Assert.Single(rig.Inputs);              // the mic keeps running
        Assert.Equal(1, rig.Monitor.StartCount);

        var voice = Signals.Sine(200, 0.2, 0.4f);
        rig.Mic.Feed(voice);
        AssertDelayedCopy(voice, rig.Monitor.Written);

        Assert.False(rig.Engine.TryRecover());
        Assert.Equal(2, rig.Monitors.Count);    // working headphones are left alone
    }

    [Fact]
    public void MonitorOnly_NoHeadphonesAtStart_TryRecoverOpensThemWhenTheyAppear()
    {
        using var rig = new Rig { CallFactory = () => throw new NoVirtualCableException(), WithMonitor = false };
        rig.Engine.HearMe = true;
        rig.Engine.FilterEnabled = false;
        rig.Engine.Start();
        Assert.True(rig.Engine.MonitorOnly);
        Assert.False(rig.Engine.TryRecover());
        Assert.Empty(rig.Monitors);

        rig.WithMonitor = true; // plugged in
        Assert.False(rig.Engine.TryRecover());
        Assert.Single(rig.Monitors);
        Assert.Single(rig.Inputs);
        var voice = Signals.Sine(200, 0.1, 0.4f);
        rig.Mic.Feed(voice);
        AssertDelayedCopy(voice, rig.Monitor.Written);
    }

    [Fact]
    public void MonitorFails_MonitorLostIsReported()
    {
        using var rig = new Rig();
        rig.Engine.HearMe = true;
        rig.Engine.Start();
        Assert.False(rig.Engine.MonitorLost);
        rig.Monitor.ThrowOnWrite = true;
        rig.Mic.Feed(Signals.Sine(200, 0.05, 0.4f));
        Assert.True(rig.Engine.MonitorLost);
        Assert.Equal(EngineState.Running, rig.Engine.State);
    }

    [Fact]
    public void CallFactoryOtherError_OutputLost()
    {
        using var rig = new Rig { CallFactory = () => throw new InvalidOperationException("busy") };
        rig.Engine.Start();
        Assert.Equal(EngineState.OutputLost, rig.Engine.State);
    }

    [Fact]
    public void Stop_DisposesEverything_TryRecoverDoesNotRestart()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        rig.Engine.Stop();
        Assert.Equal(EngineState.Stopped, rig.Engine.State);
        Assert.True(rig.Mic.Disposed);
        Assert.True(rig.Call.Disposed);
        Assert.True(rig.Monitor.Disposed);
        Assert.False(rig.Engine.TryRecover());
        Assert.Single(rig.Inputs);
        rig.Mic.Feed(Signals.Silence(0.01));
        Assert.Empty(rig.Call.Written);
    }

    [Fact]
    public void StartTwice_DoesNotRecreateDevices()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        rig.Engine.Start();
        Assert.Single(rig.Inputs);
        Assert.True(rig.Engine.TryRecover());
        Assert.Single(rig.Inputs);
    }

    [Fact]
    public void Stop_ResetsInputLevel()
    {
        using var rig = new Rig();
        rig.Engine.Start();
        rig.Mic.Feed(Signals.Sine(300, 0.05, 0.6f));
        Assert.True(rig.Engine.InputLevel > 0.5f);
        rig.Engine.Stop();
        Assert.Equal(0f, rig.Engine.InputLevel);
    }

    [Fact]
    public void FaultyStateListener_DoesNotBreakTheEngine()
    {
        using var rig = new Rig();
        rig.Engine.StateChanged += _ => throw new InvalidOperationException("bad listener");
        rig.Engine.Start();
        Assert.Equal(EngineState.Running, rig.Engine.State);
        rig.Mic.RaiseStopped(new IOException());
        Assert.Equal(EngineState.MicLost, rig.Engine.State);
    }
}
