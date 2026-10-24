using System;
using EasyVocal.Core.Dsp;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

public class VoiceShifterTests
{
    private const int Sr = AudioFormat.SampleRate;
    private static readonly double[] TwoFormants = { 700.0, 1200.0 };
    private static readonly double[] ThreeFormants = { 700.0, 1200.0, 2600.0 };

    private static VoiceShifter Make(double semis, double factor) =>
        new VoiceShifter(Sr) { PitchSemitones = semis, FormantFactor = factor };

    /// <summary>Runs in blocks, calling <paramref name="change"/> once when the block that starts at or after <paramref name="atSample"/> is reached.</summary>
    private static float[] RunWithChange(VoiceShifter fx, float[] input, int atSample, Action<VoiceShifter> change)
    {
        var o = (float[])input.Clone();
        bool changed = false;
        for (int i = 0; i < o.Length; i += AudioFormat.BlockSize)
        {
            if (!changed && i >= atSample) { change(fx); changed = true; }
            fx.Process(o.AsSpan(i, Math.Min(AudioFormat.BlockSize, o.Length - i)));
        }
        return o;
    }

    private static double MaxStep(float[] x, int from)
    {
        double m = 0;
        for (int n = Math.Max(1, from); n < x.Length; n++) m = Math.Max(m, Math.Abs(x[n] - x[n - 1]));
        return m;
    }

    [Theory]
    [InlineData(220, 12, 440)]
    [InlineData(220, -12, 110)]
    [InlineData(150, 7, 224.7)]
    public void Pitch_ShiftsFundamental(double inHz, double semis, double expectHz)
    {
        var output = Signals.Run(Make(semis, 1), Signals.PulseVowel(inHz, TwoFormants, 2));
        double hz = Signals.EstimatePitch(Signals.Slice(output, 1, 2));
        Assert.InRange(hz, expectHz * 0.97, expectHz * 1.03);
    }

    [Fact]
    public void Neutral_IsTransparentApartFromLatency()
    {
        var input = Signals.PulseVowel(150, ThreeFormants, 1.5);
        var noise = Signals.Noise(1.5, 0.05f, 11);
        for (int n = 0; n < input.Length; n++) input[n] += noise[n];
        var fx = Make(0, 1);
        var output = Signals.Run(fx, input);
        int lat = fx.LatencySamples;
        double err = 0;
        for (int n = (int)(0.2 * Sr); n + lat < input.Length; n++) err = Math.Max(err, Math.Abs(output[n + lat] - input[n]));
        Assert.True(err < 0.02, "max error " + err);
    }

    [Fact]
    public void Formant_Up_RaisesTimbreKeepsPitch()
    {
        var input = Signals.PulseVowel(150, ThreeFormants, 2);
        var output = Signals.Run(Make(0, 1.25), input);
        Assert.InRange(Signals.EstimatePitch(Signals.Slice(output, 1, 2)), 150 * 0.97, 150 * 1.03);
        double cin = Signals.Centroid(Signals.Slice(input, 0.5, 1.5), 300, 3500);
        double cout = Signals.Centroid(Signals.Slice(output, 0.5, 1.5), 300, 3500);
        Assert.True(cout > cin * 1.10, $"centroid in {cin:F0} out {cout:F0}");
    }

    [Fact]
    public void Formant_Down_LowersTimbreKeepsPitch()
    {
        var input = Signals.PulseVowel(150, ThreeFormants, 2);
        var output = Signals.Run(Make(0, 0.8), input);
        Assert.InRange(Signals.EstimatePitch(Signals.Slice(output, 1, 2)), 150 * 0.97, 150 * 1.03);
        double cin = Signals.Centroid(Signals.Slice(input, 0.5, 1.5), 300, 3500);
        double cout = Signals.Centroid(Signals.Slice(output, 0.5, 1.5), 300, 3500);
        Assert.True(cout < cin * 0.92, $"centroid in {cin:F0} out {cout:F0}");
    }

    [Fact]
    public void Latency_IsAtMost30ms()
    {
        int lat = new VoiceShifter(Sr).LatencySamples;
        Assert.InRange(lat, 1, 1440);
    }

    [Fact]
    public void Noise_StaysFiniteAndQuietOnSilence()
    {
        var input = new float[2 * Sr];
        Array.Copy(Signals.Noise(1, 0.5f, 7), input, Sr);
        var output = Signals.Run(Make(5, 1.2), input);
        Assert.True(Signals.AllFinite(output));
        Assert.True(Signals.Rms(Signals.Slice(output, 0.2, 0.9)) > 0.1, "noise should pass: " + Signals.Rms(Signals.Slice(output, 0.2, 0.9)));
        Assert.True(Signals.Peak(Signals.Slice(output, 1.1, 2)) < 1e-3, "after the noise " + Signals.Peak(Signals.Slice(output, 1.1, 2)));
    }

    [Fact]
    public void ParameterChangeMidStream_NoClicks()
    {
        var output = RunWithChange(Make(0, 1), Signals.Sine(200, 2), Sr, fx => fx.PitchSemitones = 7);
        double step = MaxStep(output, 0);
        Assert.True(step < 0.25, "max step " + step);
        Assert.True(Signals.Rms(Signals.Slice(output, 1.5, 2)) > 0.05, "the shifted voice should be audible");
    }

    /// <summary>How much the power of 5 ms frames wanders, relative to the mean power.</summary>
    private static double PowerWander(float[] x)
    {
        const int frame = 240;
        int count = x.Length / frame;
        var p = new double[count];
        double mean = 0;
        for (int f = 0; f < count; f++)
        {
            double s = 0;
            for (int i = 0; i < frame; i++) s += (double)x[f * frame + i] * x[f * frame + i];
            p[f] = s / frame;
            mean += p[f];
        }
        mean /= count;
        double var = 0;
        foreach (double v in p) var += (v - mean) * (v - mean);
        return Math.Sqrt(var / count) / mean;
    }

    [Theory]
    [InlineData(5, 1.2)]
    [InlineData(-5, 0.8)]
    [InlineData(0, 1.33)]
    public void Unvoiced_NoiseLevelDoesNotFlutter(double semis, double factor)
    {
        // Noise ("s", "f") with the voice body moved: the grain crossfades must not make the level pump.
        var input = Signals.Slice(Signals.Noise(2, 0.3f, 5), 0.5, 1.5);
        var output = Signals.Slice(Signals.Run(Make(semis, factor), Signals.Noise(2, 0.3f, 5)), 0.5, 1.5);
        double wander = PowerWander(output), reference = PowerWander(input);
        Assert.True(wander < 1.6 * reference, $"power wander {wander:F3}, input {reference:F3}");
        Assert.InRange(Signals.Rms(output) / Signals.Rms(input), 0.7, 1.3);
    }

    [Theory]
    [InlineData(7, 1.0)]
    [InlineData(5, 1.2)]
    [InlineData(-5, 0.8)]
    [InlineData(3, 1.33)]
    public void Unvoiced_HasNoPitch(double semis, double factor)
    {
        // Resampled noise grains must not repeat each other at a fixed lag (that is a comb tone on "s").
        var output = Signals.Slice(Signals.Run(Make(semis, factor), Signals.Noise(2, 0.3f, 9)), 0.5, 1.5);
        Assert.Equal(0.0, Signals.EstimatePitch(output));
        double worst = 0;
        for (int lag = 40; lag <= 800; lag++) worst = Math.Max(worst, Math.Abs(Signals.Autocorr(output, lag)));
        Assert.True(worst < 0.1, "autocorrelation " + worst);
    }

    [Theory]
    [InlineData(12, 1.0)]
    [InlineData(-12, 1.0)]
    [InlineData(5, 1.33)]
    [InlineData(-7, 0.75)]
    public void Level_StaysRoughlyTheSame(double semis, double factor)
    {
        var input = Signals.PulseVowel(160, ThreeFormants, 2);
        var output = Signals.Run(Make(semis, factor), input);
        double ratio = Signals.Rms(Signals.Slice(output, 0.5, 1.9)) / Signals.Rms(Signals.Slice(input, 0.5, 1.9));
        Assert.InRange(ratio, 0.6, 1.6);
    }

    [Fact]
    public void LoudInput_StaysFiniteAndBounded()
    {
        var output = Signals.Run(Make(-12, 0.75), Signals.Sine(120, 2, 1.0f));
        Assert.True(Signals.AllFinite(output));
        Assert.True(Signals.Peak(output) <= 2.0, "peak " + Signals.Peak(output));
    }

    [Fact]
    public void BackToNeutral_IsTransparentAgain()
    {
        var input = Signals.PulseVowel(180, ThreeFormants, 2);
        var fx = Make(-5, 1.2);
        var output = RunWithChange(fx, input, Sr, f => { f.PitchSemitones = 0; f.FormantFactor = 1; });
        int lat = fx.LatencySamples;
        double err = 0;
        for (int n = (int)(1.2 * Sr); n + lat < input.Length; n++) err = Math.Max(err, Math.Abs(output[n + lat] - input[n]));
        Assert.True(err < 0.02, "max error " + err);
        Assert.True(MaxStep(output, 0) < 0.25, "max step " + MaxStep(output, 0));
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var fx = Make(4, 1.1);
        Signals.Run(fx, Signals.PulseVowel(200, ThreeFormants, 1));
        fx.Reset();
        Assert.True(Signals.Peak(Signals.Run(fx, Signals.Silence(0.5))) < 1e-6);
    }

    [Fact]
    public void Parameters_AreClamped()
    {
        var fx = new VoiceShifter(Sr) { PitchSemitones = 40, FormantFactor = 3 };
        Assert.Equal(12, fx.PitchSemitones);
        Assert.Equal(1.33, fx.FormantFactor, 3);
        fx.PitchSemitones = double.NaN; fx.FormantFactor = double.NaN;
        Assert.Equal(0, fx.PitchSemitones);
        Assert.Equal(1, fx.FormantFactor);
        fx.FormantFactor = 0.1;
        Assert.Equal(0.75, fx.FormantFactor, 3);
    }

    [Fact]
    public void OddBlockSizes_StillShiftPitch()
    {
        // The audio device may hand over any block size; the shifter must not depend on 480.
        var fx = Make(12, 1);
        var o = Signals.PulseVowel(200, TwoFormants, 2);
        int[] sizes = { 1, 7, 128, 333, 480, 1024, 3000 };
        for (int i = 0, k = 0; i < o.Length; k++)
        {
            int len = Math.Min(sizes[k % sizes.Length], o.Length - i);
            fx.Process(o.AsSpan(i, len));
            i += len;
        }
        Assert.InRange(Signals.EstimatePitch(Signals.Slice(o, 1, 2)), 400 * 0.97, 400 * 1.03);
    }
}
