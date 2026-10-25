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
    public readonly List<ScaleConverter> Followers = new();
    public int Processed, Resets;
    public bool Disposed;

    public ScaleConverter(float gain, int latency = 4800) { Gain = gain; LatencySamples = latency; }

    public int LatencySamples { get; }

    public void Process(Span<float> mono)
    {
        if (Disposed) return;
        Processed++;
        for (int i = 0; i < mono.Length; i++) mono[i] *= Gain;
    }

    public void Reset() => Resets++;

    public IVoiceConverter CreateFollower()
    {
        var f = new ScaleConverter(Gain, LatencySamples);
        Followers.Add(f);
        return f;
    }

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
    public void Followers_get_their_own_stream_and_lose_it_with_the_voice()
    {
        var chain = new VoiceChain(Sr);
        var early = chain.CreateFollower();
        var c = new ScaleConverter(0.25f);
        chain.SetConverter(c);
        var late = chain.CreateFollower();
        Assert.Equal(2, c.Followers.Count);
        var x = Signals.Sine(330, 0.5);
        var y = Run(early, x);
        Assert.Equal(x[^1] * 0.25f, y[^1], 6);
        Assert.Equal(0, c.Processed); // the follower used its own
        Assert.True(c.Followers[0].Processed > 0);

        chain.SetConverter(new ScaleConverter(1f));
        Assert.All(c.Followers, f => Assert.True(f.Disposed, "the chain disposes the followers' streams it made"));
        Assert.False(c.Disposed, "the caller's converter stays the caller's");
        _ = late;
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
        Assert.Equal(x[at] * 0.5f, monitor.Written[at + lag], 4);
        Assert.Single(c.Followers); // the sounds' chain has its own stream through the voice

        mixer.Set("s", Signals.Sine(880, 0.3, amp: 0.2f));
        mixer.Toggle("s", 1f);
        mic.Feed(new float[Sr / 2]);
        Assert.True(c.Followers[0].Processed > 0, "the sounds went through the voice");
    }
}
