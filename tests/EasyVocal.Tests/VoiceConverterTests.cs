using System;
using System.Collections.Generic;
using System.Linq;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

/// <summary>An AI voice stand-in: scales the voice (so it is easy to recognise) and records what it was asked.</summary>
internal sealed class ScaleConverter : IVoiceConverter
{
    public readonly float Gain;
    public int Processed, Resets;
    public volatile bool Disposed;

    public ScaleConverter(float gain, int latency = 4800) { Gain = gain; LatencySamples = latency; }

    public int LatencySamples { get; }

    public void Process(Span<float> mono)
    {
        if (Disposed)
        {
            mono.Clear(); // as a real one: never the input as it is
            return;
        }
        Processed++;
        for (int i = 0; i < mono.Length; i++) mono[i] *= Gain;
    }

    public void Reset() => Resets++;

    public void Dispose() => Disposed = true;
}

/// <summary>A converter (an AI voice) in front of the voice chain: it plays first, the effects after, OFF is clean.</summary>
public class VoiceConverterTests
{
    private const int Sr = AudioFormat.SampleRate;
    private const int Block = AudioFormat.BlockSize;

    private static float[] Run(VoiceChain chain, float[] input, Action<int>? atBlock = null)
    {
        var o = (float[])input.Clone();
        int b = 0;
        for (int i = 0; i < o.Length; i += Block, b++)
        {
            atBlock?.Invoke(b);
            chain.Process(o.AsSpan(i, Math.Min(Block, o.Length - i)));
        }
        return o;
    }

    private static double MaxStep(float[] x, int from, int to)
    {
        double m = 0;
        for (int n = Math.Max(1, from); n < to; n++) m = Math.Max(m, Math.Abs(x[n] - x[n - 1]));
        return m;
    }

    [Fact]
    public void An_AI_voice_alone_is_the_converters_voice()
    {
        var chain = new VoiceChain(Sr);
        var c = new ScaleConverter(0.5f);
        chain.SetConverter(c);
        var x = Signals.Sine(220, 1);
        var y = Run(chain, x);
        int from = Sr / 10; // the 40 ms crossfade is over
        for (int i = from; i < x.Length; i++) Assert.Equal(x[i] * 0.5f, y[i], 6);
        Assert.Equal(4800, chain.LatencySamples);
        Assert.Same(c, chain.Converter);
    }

    [Fact]
    public void The_classic_effects_play_on_the_converted_voice()
    {
        var tone = new VoiceSliders { Bass = 0.6, Treble = -0.4 }; // linear shelves: half the input → half the output
        var plain = new VoiceChain(Sr);
        plain.Apply(tone);
        var withAi = new VoiceChain(Sr);
        withAi.Apply(tone);
        withAi.SetConverter(new ScaleConverter(0.5f));
        var x = Signals.PulseVowel(150, new[] { 700.0, 1200, 2600 }, 1);
        var a = Run(plain, x);
        var b = Run(withAi, x);
        for (int i = Sr / 4; i < x.Length; i++) Assert.Equal(a[i] * 0.5f, b[i], 4);
    }

    [Fact]
    public void Off_and_Normal_give_the_clean_voice_without_a_click()
    {
        var chain = new VoiceChain(Sr);
        var c = new ScaleConverter(0.5f);
        chain.SetConverter(c);
        var x = Signals.Sine(220, 1.5);
        var y = Run(chain, x, b =>
        {
            if (b == 40) chain.Bypass = true;          // OFF
            if (b == 80) chain.Bypass = false;         // ON again
            if (b == 120) chain.SetConverter(null);    // a classic voice
        });
        int at = 40 * Block + Sr / 10;
        Assert.Equal(x.AsSpan(at, Block * 10).ToArray(), y.AsSpan(at, Block * 10).ToArray());
        Assert.Equal(0, chain.LatencySamples); // the last state: no converter
        int end = 120 * Block + Sr / 10;
        Assert.Equal(x.AsSpan(end).ToArray(), y.AsSpan(end).ToArray());
        double sine = MaxStep(x, 1, x.Length);
        Assert.True(MaxStep(y, 1, y.Length) <= sine * 1.01, "no click at any switch");
    }

    [Fact]
    public void Off_reports_no_latency_and_a_reset_reaches_the_converter()
    {
        var chain = new VoiceChain(Sr);
        var c = new ScaleConverter(0.5f, latency: 1000);
        chain.SetConverter(c);
        Assert.Equal(1000, chain.LatencySamples);
        chain.Bypass = true;
        Assert.Equal(0, chain.LatencySamples);
        chain.Bypass = false;
        chain.Reset();
        Assert.Equal(1, c.Resets);
    }

    [Fact]
    public void A_converter_let_go_is_disposed_only_once_the_chain_stopped_using_it()
    {
        var chain = new VoiceChain(Sr);
        var c = new ScaleConverter(0.5f);
        chain.SetConverter(c);
        var x = Signals.Sine(220, 0.3);
        Run(chain, x);
        chain.SetConverter(null);
        chain.Retire(c);
        System.Threading.Thread.Sleep(300);
        Assert.False(c.Disposed, "the audio thread still plays it (it hasn't faded it out yet)");
        Assert.True(chain.Holds(c));
        var y = Run(chain, x); // the 40 ms crossfade away from it
        Assert.False(chain.Holds(c));
        for (int i = 0; i < 100 && !c.Disposed; i++) System.Threading.Thread.Sleep(10);
        Assert.True(c.Disposed, "disposed once the chain let go of it");
        Assert.Equal(x[^1], y[^1], 6);

        // a follower (the sounds' chain) never gets a converter
        var follower = chain.CreateFollower();
        var d = new ScaleConverter(0.25f);
        chain.SetConverter(d);
        var z = Run(follower, x);
        Assert.Equal(x[^1], z[^1], 6);
        Assert.Equal(0, follower.LatencySamples);

        // once the audio stopped for good, whatever waits is disposed at once
        chain.SetConverter(null);
        chain.Retire(d);
        chain.RetireAllNow();
        Assert.True(d.Disposed);
        var e = new ScaleConverter(1f);
        chain.Retire(e);
        Assert.True(e.Disposed);
    }

    [Fact]
    public void Through_the_engine_the_call_hear_me_and_the_sounds_get_the_AI_voice()
    {
        var chain = new VoiceChain(Sr);
        var mixer = new SoundMixer();
        var mic = new FakeInput();
        var call = new FakeOutput();
        var monitor = new FakeOutput();
        using var engine = new AudioEngine(() => mic, () => call, () => monitor, chain, mixer)
        {
            HearMe = true, HearSounds = true, SoundsUseVoice = true,
        };
        var c = new ScaleConverter(0.5f);
        chain.SetConverter(c);
        engine.Start();
        var x = Signals.Sine(220, 0.5, amp: 0.2f);
        mic.Feed(x);
        int lag = new Limiter(Sr).LatencySamples;
        var got = call.Written;
        int at = Sr / 4;
        Assert.Equal(x[at] * 0.5f, got[at + lag], 4);
        Assert.Equal(x[at], monitor.Written[at + lag], 4); // Hear me: the voice as spoken (no delay to talk over)

        engine.HearConvertedVoice = true;
        int from = monitor.Written.Length;
        mic.Feed(x);
        Assert.Equal(x[at] * 0.5f, monitor.Written[from + at + lag], 4); // asked for: the AI voice

        mixer.Set("s", Signals.Sine(880, 0.3, amp: 0.2f));
        int before = c.Processed;
        mixer.Toggle("s", 1f);
        int callFrom = call.Written.Length;
        mic.Feed(new float[Sr / 4]);
        var withSound = call.Written.AsSpan(callFrom + lag + 480, 4800).ToArray();
        double peak = withSound.Max(Math.Abs);
        Assert.InRange(peak, 0.07, 0.13); // the 0.2 sound at the voice's 0.5: it went into the AI voice's own stream
        Assert.True(c.Processed > before);
    }

    [Fact]
    public void With_an_AI_voice_and_sounds_through_it_the_headphones_still_play_the_sounds()
    {
        var chain = new VoiceChain(Sr);
        var mixer = new SoundMixer();
        var mic = new FakeInput();
        var call = new FakeOutput();
        var monitor = new FakeOutput();
        using var engine = new AudioEngine(() => mic, () => call, () => monitor, chain, mixer)
        {
            HearMe = false, HearSounds = true, SoundsUseVoice = true,
        };
        var c = new ScaleConverter(0.5f);
        chain.SetConverter(c);
        engine.Start();
        mic.Feed(new float[Sr / 10]); // the crossfade into the AI voice is over
        mixer.Set("s", Signals.Sine(880, 0.3, amp: 0.2f));
        mixer.Toggle("s", 1f);
        int from = monitor.Written.Length;
        mic.Feed(new float[Sr / 4]);
        double peak = monitor.Written.AsSpan(from + 960, 4800).ToArray().Max(Math.Abs);
        Assert.InRange(peak, 0.15, 0.25); // Hear sounds: the sound as it is (the call gets it through the AI voice)

        // Hear me with the AI voice itself: that voice carries the sounds already, the copy isn't added on top
        engine.HearMe = true;
        engine.HearConvertedVoice = true;
        mic.Feed(new float[Sr / 4]); // the sound is over
        Assert.False(mixer.IsPlaying("s"));
        mixer.Toggle("s", 1f);
        from = monitor.Written.Length;
        mic.Feed(new float[Sr / 4]);
        peak = monitor.Written.AsSpan(from + 2400, 4800).ToArray().Max(Math.Abs);
        Assert.InRange(peak, 0.07, 0.13); // once, through the AI voice (0.5)
    }

    [Fact]
    public void A_warmed_converter_is_fed_unheard_then_swapped_in_and_OFF_then_ON_starts_it_afresh()
    {
        var chain = new VoiceChain(Sr);
        var a = new ScaleConverter(0.5f);
        chain.SetConverter(a);
        var x = Signals.Sine(330, 0.2);
        Run(chain, x);
        var b = new ScaleConverter(0.25f);
        chain.Warm(b);
        var y = Run(chain, x);
        Assert.True(b.Processed > 0, "the next voice is already fed");
        Assert.Equal(x[^1] * 0.5f, y[^1], 5); // ...while the previous one is still the one heard
        Assert.Same(b, chain.Warming);
        chain.SetConverter(b);
        Assert.Null(chain.Warming);
        y = Run(chain, x);
        Assert.Equal(x[^1] * 0.25f, y[^1], 5);
        Assert.Equal(0, b.Resets); // warmed: its stream goes on
        chain.Bypass = true;
        Run(chain, x);
        chain.Bypass = false;
        Run(chain, x);
        Assert.Equal(1, b.Resets); // back from OFF: a fresh stream, not what it held before
    }
}
