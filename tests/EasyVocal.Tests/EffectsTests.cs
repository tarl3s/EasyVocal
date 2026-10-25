using System;
using System.Collections.Generic;
using EasyVocal.Core.Dsp;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

public class EffectsTests
{
    private const int Sr = AudioFormat.SampleRate;

    public static IEnumerable<object[]> AllEffects => new[]
    {
        new object[] { (Func<IAudioEffect>)(() => new EchoEffect(Sr)) },
        new object[] { (Func<IAudioEffect>)(() => new RadioEffect(Sr)) },
        new object[] { (Func<IAudioEffect>)(() => new DistortionEffect(Sr)) },
        new object[] { (Func<IAudioEffect>)(() => new RobotEffect(Sr)) },
        new object[] { (Func<IAudioEffect>)(() => new WhisperEffect(Sr)) },
        new object[] { (Func<IAudioEffect>)(() => new GrowlEffect(Sr)) },
        new object[] { (Func<IAudioEffect>)(() => new ReverbEffect(Sr)) },
        new object[] { (Func<IAudioEffect>)(() => new VocoderEffect(Sr)) },
    };

    private static IAudioEffect Make(Func<IAudioEffect> make, double amount)
    {
        var fx = make();
        if (fx is not IAmountEffect a) throw new InvalidOperationException("unknown effect " + fx.GetType().Name);
        a.Amount = amount;
        return fx;
    }

    [Theory, MemberData(nameof(AllEffects))]
    public void AmountZero_LeavesBufferUntouched(Func<IAudioEffect> make)
    {
        var fx = Make(make, 0);
        var input = Signals.Noise(0.5, 0.5f, 1);
        var output = Signals.Run(fx, input);
        Assert.Equal(input, output); // bit-exact
    }

    [Theory, MemberData(nameof(AllEffects))]
    public void Silence_StaysSilent(Func<IAudioEffect> make)
    {
        var output = Signals.Run(Make(make, 1), Signals.Silence(1));
        Assert.True(Signals.Peak(output) < 1e-4);
    }

    [Theory, MemberData(nameof(AllEffects))]
    public void LoudInput_OutputFiniteAndBounded(Func<IAudioEffect> make)
    {
        var output = Signals.Run(Make(make, 1), Signals.Sine(300, 2, 1.0f));
        Assert.True(Signals.AllFinite(output));
        Assert.True(Signals.Peak(output) <= 1.5, "peak " + Signals.Peak(output));
    }

    [Theory, MemberData(nameof(AllEffects))]
    public void Reset_ClearsTails(Func<IAudioEffect> make)
    {
        var fx = Make(make, 1);
        Signals.Run(fx, Signals.Noise(1, 0.5f, 2));
        fx.Reset();
        var output = Signals.Run(fx, Signals.Silence(0.5));
        Assert.True(Signals.Peak(output) < 1e-4);
    }

    [Fact]
    public void Echo_RepeatsSoundAfterAShortDelay()
    {
        var input = new float[(int)(0.6 * Sr)];
        var burst = Signals.Noise(0.05, 0.5f, 3);
        Array.Copy(burst, input, burst.Length);
        var output = Signals.Run(Make(() => new EchoEffect(Sr), 1), input);
        double tail = Signals.Rms(Signals.Slice(output, 0.15, 0.4));
        Assert.True(tail > 0.05 * Signals.Rms(burst), $"tail {tail} burst {Signals.Rms(burst)}");
    }

    [Fact]
    public void Radio_CutsLowsAndHighs()
    {
        double Ratio(double hz)
        {
            var input = Signals.Sine(hz, 2);
            var output = Signals.Run(Make(() => new RadioEffect(Sr), 1), input);
            return Signals.Magnitude(Signals.Slice(output, 0.5, 1.5), hz) / Signals.Magnitude(Signals.Slice(input, 0.5, 1.5), hz);
        }
        Assert.True(Ratio(100) < 0.3, "100 Hz " + Ratio(100));
        Assert.True(Ratio(1000) > 0.6, "1 kHz " + Ratio(1000));
        Assert.True(Ratio(8000) < 0.3, "8 kHz " + Ratio(8000));
    }

    [Fact]
    public void Radio_CracklesOnlyWhileTheVoiceIsThere()
    {
        // Talking (noise as a stand-in) then silence: the silent second half must be dead quiet.
        var input = new float[2 * Sr];
        Array.Copy(Signals.Noise(1, 0.3f, 4), input, Sr);
        var output = Signals.Run(Make(() => new RadioEffect(Sr), 1), input);
        Assert.True(Signals.Peak(Signals.Slice(output, 1.3, 2)) < 1e-3, "hiss in silence " + Signals.Peak(Signals.Slice(output, 1.3, 2)));
    }

    [Fact]
    public void Distortion_AddsHarmonics()
    {
        var input = Signals.Sine(300, 2);
        var output = Signals.Run(Make(() => new DistortionEffect(Sr), 1), input);
        double o = Signals.Magnitude(Signals.Slice(output, 0.5, 1.5), 900);
        double i = Signals.Magnitude(Signals.Slice(input, 0.5, 1.5), 900);
        Assert.True(o > 5 * i, $"out {o} in {i}");
        Assert.True(o > 0.01, "the harmonic should be clearly audible: " + o);
    }

    [Fact]
    public void Robot_MovesEnergyOffTheFundamental()
    {
        var input = Signals.Sine(200, 2);
        var output = Signals.Run(Make(() => new RobotEffect(Sr), 1), input);
        double ratio = Signals.Magnitude(Signals.Slice(output, 0.5, 1.5), 200) / Signals.Magnitude(Signals.Slice(input, 0.5, 1.5), 200);
        Assert.True(ratio < 0.6, "ratio " + ratio);
    }

    [Fact]
    public void Whisper_RemovesPitch()
    {
        var input = Signals.PulseVowel(150, new[] { 700.0, 1200.0, 2600.0 }, 2);
        Assert.InRange(Signals.EstimatePitch(input), 145, 155);
        var output = Signals.Run(Make(() => new WhisperEffect(Sr), 1), input);
        Assert.True(Signals.Rms(output) > 0.01, "the whisper should be audible: " + Signals.Rms(output));
        var steady = Signals.Slice(output, 0.5, 1.9);
        double lag = Sr / 150.0, best = 0;
        for (int l = (int)lag - 3; l <= (int)lag + 3; l++) best = Math.Max(best, Signals.Autocorr(steady, l));
        Assert.True(best < 0.3, "autocorrelation at the pitch lag " + best);
    }
}
