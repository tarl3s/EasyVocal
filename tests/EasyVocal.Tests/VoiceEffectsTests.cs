using System;
using System.Collections.Generic;
using System.Linq;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

/// <summary>The building blocks behind the built-in voices' characters: growl, big room, tone, vocoder, wobble, monotone, octave layer.</summary>
public class VoiceEffectsTests
{
    private const int Sr = AudioFormat.SampleRate;
    private const int Block = AudioFormat.BlockSize;
    private static readonly double[] Formants = { 700.0, 1200.0, 2600.0 };

    private static float[] RunChain(VoiceChain chain, float[] input)
    {
        var o = (float[])input.Clone();
        for (int i = 0; i < o.Length; i += Block) chain.Process(o.AsSpan(i, Math.Min(Block, o.Length - i)));
        return o;
    }

    private static float[] RunChain(VoiceSliders sliders, float[] input)
    {
        var chain = new VoiceChain(Sr);
        chain.Apply(sliders);
        return RunChain(chain, input);
    }

    /// <summary>Pitch of every 0.1 s piece between two times (0 where none was found).</summary>
    private static List<double> PitchTrack(float[] x, double from, double to)
    {
        var list = new List<double>();
        for (double t = from; t + 0.1 <= to; t += 0.1) list.Add(Signals.EstimatePitch(Signals.Slice(x, t, t + 0.1)));
        return list;
    }

    private static double SpreadSemitones(List<double> hz)
    {
        var st = hz.Where(h => h > 0).Select(h => 12 * Math.Log2(h)).ToList();
        return st.Count < 3 ? 0 : st.Max() - st.Min();
    }

    [Fact]
    public void Growl_MakesTheLevelFlutter()
    {
        var input = Signals.Sine(200, 1, 0.4f);
        var output = Signals.Run(new GrowlEffect(Sr) { Amount = 1 }, input);
        // RMS per 4 ms: steady for the sine, fluttering 30-50 times a second with the growl.
        var levels = Enumerable.Range(25, 200).Select(k => Signals.Rms(Signals.Slice(output, k * 0.004, (k + 1) * 0.004))).ToList();
        double mean = levels.Average(), sd = Math.Sqrt(levels.Average(l => (l - mean) * (l - mean)));
        Assert.True(sd / mean > 0.25, "level flutter " + sd / mean);
        Assert.InRange(Signals.Rms(Signals.Slice(output, 0.1, 1)) / Signals.Rms(input), 0.6, 1.5);
    }

    [Fact]
    public void BigRoom_RingsLongerWhenBigger()
    {
        var burst = Signals.Noise(0.2, 0.4f, 3).Concat(Signals.Silence(2)).ToArray();
        double Tail(double amount) => Signals.Rms(Signals.Slice(Signals.Run(new ReverbEffect(Sr) { Amount = amount }, burst), 0.7, 1.2));
        double small = Tail(0.2), big = Tail(0.95);
        Assert.True(big > 10 * small, $"tail small {small:0.00000} big {big:0.00000}");
        Assert.True(ReverbEffect.DecaySeconds(0.95) > 2.5 && ReverbEffect.DecaySeconds(0.1) < 0.6);
    }

    [Fact]
    public void BigRoom_WaitsLongerBeforeTheFirstReflectionWhenBigger()
    {
        int FirstEcho(double amount)
        {
            var x = new float[Sr / 2];
            x[100] = 0.8f;
            var y = Signals.Run(new ReverbEffect(Sr) { Amount = amount }, x);
            for (int n = 102; n < y.Length; n++)
                if (Math.Abs(y[n]) > 1e-4) return n - 100;
            return int.MaxValue;
        }
        int small = FirstEcho(0.2), big = FirstEcho(1);
        Assert.True(big - small > Sr * 0.05, $"first reflection after {small} vs {big} samples");
    }

    [Fact]
    public void Tone_BassAndTreble_ShelveTheirEnd()
    {
        double Gain(double bass, double treble, double hz)
        {
            var input = Signals.Sine(hz, 0.5, 0.3f);
            var y = Signals.Run(new ToneEffect(Sr) { Bass = bass, Treble = treble }, input);
            return 20 * Math.Log10(Signals.Rms(Signals.Slice(y, 0.2, 0.5)) / Signals.Rms(Signals.Slice(input, 0.2, 0.5)));
        }
        Assert.InRange(Gain(1, 0, 80), 7, 10);
        Assert.InRange(Gain(1, 0, 6000), -1, 1);
        Assert.InRange(Gain(0, -1, 7000), -10, -7);
        Assert.InRange(Gain(0, -1, 100), -1, 1);
        var noise = Signals.Noise(0.3, 0.5f, 4);
        Assert.Equal(noise, Signals.Run(new ToneEffect(Sr), noise)); // both in the middle: bit-exact
    }

    [Theory]
    [InlineData(130)]
    [InlineData(220)]
    public void Vocoder_SaysTheWordsOnOneFixedNote(double inputHz)
    {
        var output = Signals.Run(new VocoderEffect(Sr) { Amount = 1 }, Signals.PulseVowel(inputHz, Formants, 1));
        Assert.InRange(Signals.EstimatePitch(Signals.Slice(output, 0.3, 1)), VocoderEffect.CarrierHz * 0.95, VocoderEffect.CarrierHz * 1.05);
        Assert.True(Signals.Rms(output) > 0.02, "the vocoder should be audible: " + Signals.Rms(output));
    }

    [Fact]
    public void Monotone_FlattensTheMelody()
    {
        // Speech-like intonation: up and down 4 semitones around 150 Hz, a little faster than once a second.
        var glide = Signals.PulseVowel(t => 150 * Math.Pow(2, 4 / 12.0 * Math.Sin(2 * Math.PI * 1.3 * t)), Formants, 4);
        var flat = Signals.Run(new VoiceShifter(Sr) { Monotone = 1 }, glide);
        double before = SpreadSemitones(PitchTrack(glide, 1.5, 3.9)), after = SpreadSemitones(PitchTrack(flat, 1.5, 3.9));
        Assert.True(before > 6, "the input moves " + before);
        Assert.True(after < 0.35 * before, $"monotone left {after:0.0} of {before:0.0} semitones");
    }

    [Fact]
    public void Wobble_MakesASteadyPitchWaver()
    {
        var steady = Signals.PulseVowel(150, Formants, 3);
        double plain = SpreadSemitones(PitchTrack(Signals.Run(new VoiceShifter(Sr) { PitchSemitones = 0.01 }, steady), 0.5, 2.9));
        double wobbly = SpreadSemitones(PitchTrack(Signals.Run(new VoiceShifter(Sr) { Wobble = 1 }, steady), 0.5, 2.9));
        Assert.True(plain < 0.4, "steady " + plain);
        Assert.True(wobbly > 1.2, "wobble " + wobbly);
    }

    [Fact]
    public void VoicedOnly_LeavesHissOut()
    {
        var hiss = Signals.Run(new VoiceShifter(Sr, -24) { PitchSemitones = -18, VoicedOnly = true }, Signals.Noise(1, 0.3f, 5));
        Assert.True(Signals.Rms(Signals.Slice(hiss, 0.2, 1)) < 0.01, "hiss " + Signals.Rms(hiss));
        var vowel = Signals.Run(new VoiceShifter(Sr, -24) { PitchSemitones = -18, VoicedOnly = true }, Signals.PulseVowel(200, Formants, 1));
        Assert.True(Signals.Rms(Signals.Slice(vowel, 0.2, 1)) > 0.02, "voiced " + Signals.Rms(vowel));
    }

    [Fact]
    public void LayerShifter_GoesBelowMinus12()
    {
        Assert.Equal(-12, new VoiceShifter(Sr) { PitchSemitones = -20 }.PitchSemitones);
        Assert.Equal(-20, new VoiceShifter(Sr, -24) { PitchSemitones = -20 }.PitchSemitones);
    }

    [Fact]
    public void Octave_AddsAVoiceAnOctaveBelow()
    {
        var input = Signals.PulseVowel(160, Formants, 2);
        var plain = RunChain(new VoiceSliders { Pitch = -0.25 }, input);
        var layered = RunChain(new VoiceSliders { Pitch = -0.25, Octave = 1 }, input);
        double f0 = 160 * Math.Pow(2, -3 / 12.0);
        double before = Signals.Magnitude(Signals.Slice(plain, 0.5, 2), f0 / 2), after = Signals.Magnitude(Signals.Slice(layered, 0.5, 2), f0 / 2);
        Assert.True(after > 5 * before + 0.005, $"energy an octave below: {before:0.0000} → {after:0.0000}");
        Assert.True(Signals.Magnitude(Signals.Slice(layered, 0.5, 2), f0) > 0.3 * Signals.Magnitude(Signals.Slice(plain, 0.5, 2), f0), "the voice itself is still there");
    }

    [Fact]
    public void NewSliders_AreNeutralAtZero_AndNeedTheShifterOnlyForPitchThings()
    {
        Assert.True(new VoiceSliders().IsNeutral);
        foreach (var make in new Func<VoiceSliders>[]
                 {
                     () => new() { Growl = 0.5 }, () => new() { Room = 0.5 }, () => new() { Bass = -0.5 }, () => new() { Treble = 0.5 },
                     () => new() { Vocoder = 0.5 },
                 })
        {
            var s = make();
            Assert.False(s.IsNeutral);
            Assert.False(s.Shifts);
            var chain = new VoiceChain(Sr);
            chain.Apply(s);
            Assert.Equal(0, chain.LatencySamples);
        }
        foreach (var s in new VoiceSliders[] { new() { Octave = 0.5 }, new() { Wobble = 0.5 }, new() { Monotone = 0.5 } })
        {
            Assert.True(s.Shifts);
            var chain = new VoiceChain(Sr);
            chain.Apply(s);
            Assert.Equal(new VoiceShifter(Sr).LatencySamples, chain.LatencySamples);
        }
        var wild = new VoiceSliders { Growl = 3, Room = -1, Octave = double.NaN, Bass = -7, Treble = 2, Wobble = 9, Monotone = -1, Vocoder = 1.5 }.Clamp();
        Assert.Equal((1.0, 0.0, 0.0, -1.0, 1.0, 1.0, 0.0, 1.0), (wild.Growl, wild.Room, wild.Octave, wild.Bass, wild.Treble, wild.Wobble, wild.Monotone, wild.Vocoder));
    }

    [Fact]
    public void Follower_PlaysTheLeadersVoiceOnItsOwnSignal()
    {
        var leader = new VoiceChain(Sr);
        var follower = leader.CreateFollower();
        leader.Apply(new VoiceSliders { Pitch = 0.5 });
        follower.Apply(new VoiceSliders()); // ignored: the follower plays the leader's voice
        var sine = Signals.Sine(220, 1.5, 0.4f);
        Assert.InRange(Signals.EstimatePitch(Signals.Slice(RunChain(follower, sine), 0.5, 1.5)), 290, 330);
        Assert.Equal(leader.LatencySamples, follower.LatencySamples);

        leader.Bypass = true;
        var clean = RunChain(follower, sine);
        int from = Sr / 10;
        Assert.Equal(sine.AsSpan(from).ToArray(), clean.AsSpan(from).ToArray());
        Assert.True(follower.Bypass);
    }

    [Fact]
    public void DraggingTheNewSliders_NoClicks()
    {
        var chain = new VoiceChain(Sr);
        var s = new VoiceSliders { Pitch = 0.2 };
        chain.Apply(s);
        var input = Signals.Sine(220, 6, 0.5f);
        var o = (float[])input.Clone();
        int b = 0;
        for (int i = 0; i < o.Length; i += Block, b++)
        {
            int step = b / 20;
            double v = (b % 20) * 0.05;
            if (step % 2 == 1) v = 1 - v;
            var t = s.Clone();
            switch (step / 2)
            {
                case 0: t.Growl = v; break;
                case 1: t.Room = v; break;
                case 2: t.Bass = v; break;
                case 3: t.Treble = -v; break;
                case 4: t.Wobble = v; break;
                case 5: t.Monotone = v; break;
                case 6: t.Octave = v; break;
                case 7: t.Vocoder = v; break;
            }
            chain.Apply(t);
            chain.Process(o.AsSpan(i, Math.Min(Block, o.Length - i)));
        }
        Assert.True(Signals.AllFinite(o));
        double m = 0;
        for (int n = 481; n < o.Length; n++) m = Math.Max(m, Math.Abs(o[n] - o[n - 1]));
        Assert.True(m < 0.35, "max step " + m);
    }
}
