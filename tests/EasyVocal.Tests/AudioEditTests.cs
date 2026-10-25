using System;
using System.Linq;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

public class AudioEditTests
{
    private const int Sr = AudioFormat.SampleRate;

    /// <summary>0, 1, 2, … so every sample says where it came from.</summary>
    private static float[] Ramp(int n)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = i;
        return x;
    }

    // ---------- ranges ----------

    [Fact]
    public void Cut_RemovesTheRange()
    {
        var y = AudioEdit.Cut(Ramp(10), 3, 4);
        Assert.Equal(new float[] { 0, 1, 2, 7, 8, 9 }, y);
    }

    [Fact]
    public void Trim_KeepsOnlyTheRange()
    {
        Assert.Equal(new float[] { 3, 4, 5, 6 }, AudioEdit.Trim(Ramp(10), 3, 4));
    }

    [Fact]
    public void Split_GivesBothPartsInOrder()
    {
        var (a, b) = AudioEdit.Split(Ramp(10), 4);
        Assert.Equal(new float[] { 0, 1, 2, 3 }, a);
        Assert.Equal(new float[] { 4, 5, 6, 7, 8, 9 }, b);
    }

    [Theory]
    [InlineData(-5, 3, 0, 0)]
    [InlineData(8, 10, 8, 2)]
    [InlineData(3, -1, 3, 0)]
    [InlineData(20, 5, 10, 0)]
    public void Ranges_AreClampedToTheSound(int start, int count, int wantStart, int wantCount)
    {
        Assert.Equal((wantStart, wantCount), AudioEdit.Clamp(10, start, count));
        Assert.Equal(10 - wantCount, AudioEdit.Cut(Ramp(10), start, count).Length);
    }

    [Fact]
    public void OnRange_OnlyChangesTheRange_AndCanChangeItsLength()
    {
        var y = AudioEdit.OnRange(Ramp(6), 2, 2, part => new float[] { -1, -1, -1 });
        Assert.Equal(new float[] { 0, 1, -1, -1, -1, 4, 5 }, y);
    }

    // ---------- speed ----------

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Speed_KeepPitch_ScalesLengthAndKeepsPitch(double speed)
    {
        var x = Signals.PulseVowel(150, new[] { 700.0, 1200, 2600 }, 1.5);
        var y = AudioEdit.ChangeSpeed(x, speed, keepPitch: true);
        Assert.InRange(y.Length, (int)(x.Length / speed) - 2, (int)(x.Length / speed) + 2);
        Assert.InRange(Signals.EstimatePitch(y), 150 * 0.97, 150 * 1.03);
        Assert.True(Signals.AllFinite(y));
        Assert.InRange(Signals.Rms(y), Signals.Rms(x) * 0.6, Signals.Rms(x) * 1.4); // no big level jump
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void Speed_ChangePitch_ScalesLengthAndPitchTogether(double speed)
    {
        var x = Signals.Sine(200, 1.5);
        var y = AudioEdit.ChangeSpeed(x, speed, keepPitch: false);
        Assert.InRange(y.Length, (int)(x.Length / speed) - 2, (int)(x.Length / speed) + 2);
        Assert.InRange(Signals.EstimatePitch(y), 200 * speed * 0.97, 200 * speed * 1.03);
    }

    [Fact]
    public void Speed_One_IsAnExactCopy()
    {
        var x = Signals.Sine(300, 0.2);
        var y = AudioEdit.ChangeSpeed(x, 1, keepPitch: true);
        Assert.Equal(x, y);
        Assert.NotSame(x, y);
    }

    [Fact]
    public void Speed_VeryShortSound_StillWorks()
    {
        var y = AudioEdit.ChangeSpeed(Signals.Sine(300, 0.01), 2, keepPitch: true);
        Assert.InRange(y.Length, 238, 242);
        Assert.Empty(AudioEdit.ChangeSpeed(Array.Empty<float>(), 1.5, keepPitch: true));
        Assert.Empty(AudioEdit.ChangeSpeed(Array.Empty<float>(), 1.5, keepPitch: false));
    }

    // ---------- pitch ----------

    [Theory]
    [InlineData(5, 160)]
    [InlineData(-5, 160)]
    [InlineData(12, 160)]
    [InlineData(AudioEdit.MaxPitchShift, 160)]
    [InlineData(-AudioEdit.MaxPitchShift, 320)] // two octaves down from 320 Hz: 80 Hz, still a voice
    public void ShiftPitch_MovesThePitch_AndKeepsTheLength(int semitones, double from)
    {
        var x = Signals.PulseVowel(from, new[] { 700.0, 1200 }, 1.5);
        var y = AudioEdit.ShiftPitch(x, semitones);
        Assert.Equal(x.Length, y.Length);
        double want = from * Math.Pow(2, semitones / 12.0);
        Assert.InRange(Signals.EstimatePitch(y), want * 0.97, want * 1.03);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(-5)]
    public void ShiftPitch_ShortClip_StillMovesThePitch(int semitones)
    {
        var x = Signals.Sine(300, 2800.0 / Sr); // under two WSOLA frames (3072 samples)
        var y = AudioEdit.ShiftPitch(x, semitones);
        Assert.Equal(x.Length, y.Length);
        double want = 300 * Math.Pow(2, semitones / 12.0);
        Assert.InRange(Signals.EstimatePitch(y), want * 0.95, want * 1.05);
        Assert.Empty(AudioEdit.ShiftPitch(Array.Empty<float>(), semitones));
    }

    [Fact]
    public void ShiftPitch_GoesTwoOctavesAtMost()
    {
        var x = Signals.Sine(300, 0.1);
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioEdit.ShiftPitch(x, AudioEdit.MaxPitchShift + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioEdit.ShiftPitch(x, -AudioEdit.MaxPitchShift - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioEdit.ShiftPitch(x, double.NaN));
    }

    // ---------- volume ----------

    [Fact]
    public void Louder_ThenQuieter_GivesTheSoundBack()
    {
        var x = Signals.Sine(440, 0.1, 0.3f);
        var y = AudioEdit.Gain(AudioEdit.Gain(x, AudioEdit.LouderFactor), 1 / AudioEdit.LouderFactor);
        for (int i = 0; i < x.Length; i++) Assert.Equal(x[i], y[i], 5);
        Assert.InRange(Signals.Peak(AudioEdit.Gain(x, AudioEdit.LouderFactor)), 0.3 * 1.41, 0.3 * 1.42);
    }

    [Fact]
    public void Normalize_PutsThePeakAtMinusOneDb()
    {
        var y = AudioEdit.Normalize(Signals.Sine(440, 0.1, 0.1f));
        Assert.InRange(Signals.Peak(y), 0.89, 0.892);
        Assert.Equal(new float[100], AudioEdit.Normalize(new float[100])); // silence stays silence
    }

    // ---------- fades, reverse ----------

    [Fact]
    public void FadeIn_StartsAtZero_AndEndsAtFullVolume()
    {
        var x = new float[1000];
        Array.Fill(x, 0.5f);
        var y = AudioEdit.FadeIn(x);
        Assert.Equal(0f, y[0]);
        Assert.Equal(0.5f, y[^1], 5);
        Assert.Equal(0.25f, y[500], 2);
        for (int i = 1; i < y.Length; i++) Assert.True(y[i] >= y[i - 1]);
    }

    [Fact]
    public void FadeOut_EndsAtZero()
    {
        var x = new float[1000];
        Array.Fill(x, 0.5f);
        var y = AudioEdit.FadeOut(x);
        Assert.Equal(0.5f, y[0], 5);
        Assert.Equal(0f, y[^1]);
    }

    [Fact]
    public void DefaultFadeRange_IsHalfASecondAtTheEdge()
    {
        Assert.Equal((0, Sr / 2), AudioEdit.DefaultFadeRange(3 * Sr, fadeIn: true));
        Assert.Equal((3 * Sr - Sr / 2, Sr / 2), AudioEdit.DefaultFadeRange(3 * Sr, fadeIn: false));
        Assert.Equal((0, 100), AudioEdit.DefaultFadeRange(100, fadeIn: false)); // shorter than that: all of it
    }

    [Fact]
    public void FadeOnARange_LeavesTheRestAlone()
    {
        var x = new float[100];
        Array.Fill(x, 1f);
        var y = AudioEdit.OnRange(x, 90, 10, AudioEdit.FadeOut);
        for (int i = 0; i < 90; i++) Assert.Equal(1f, y[i]);
        Assert.Equal(0f, y[99]);
    }

    [Fact]
    public void Reverse_TwiceGivesTheSoundBack()
    {
        var x = Ramp(7);
        Assert.Equal(new float[] { 6, 5, 4, 3, 2, 1, 0 }, AudioEdit.Reverse(x));
        Assert.Equal(x, AudioEdit.Reverse(AudioEdit.Reverse(x)));
    }

    // ---------- echo ----------

    [Theory]
    [InlineData(AudioEdit.EchoStrength.Light)]
    [InlineData(AudioEdit.EchoStrength.Strong)]
    public void Echo_RepeatsTheSoundLater_AndQuieter(AudioEdit.EchoStrength strength)
    {
        var x = new float[Sr / 2];
        x[0] = 1f; // a click
        var y = AudioEdit.Echo(x, strength);
        Assert.True(y.Length > x.Length);
        Assert.Equal(1f, y[0]);
        int delay = (int)Math.Round(0.22 * Sr);
        Assert.InRange(y[delay], 0.3f, 0.9f);
        Assert.True(y[2 * delay] < y[delay]);
        Assert.True(Math.Abs(y[^1]) < 0.01f); // the tail has died away
    }

    [Fact]
    public void EchoRange_RingsOnOverWhatFollows()
    {
        var x = new float[Sr * 2];
        x[1000] = 1f;
        var y = AudioEdit.EchoRange(x, 0, Sr / 10, AudioEdit.EchoStrength.Light);
        Assert.Equal(x.Length, y.Length); // the tail fits in the sound
        Assert.True(y[1000 + (int)Math.Round(0.22 * Sr)] > 0.3f);
    }

    // ---------- a voice ----------

    [Fact]
    public void ApplyVoice_LinesUpWithTheOriginal()
    {
        // Silence, a vowel, silence: with a shifting voice the vowel must start where it started.
        var vowel = Signals.PulseVowel(150, new[] { 700.0, 1200 }, 0.6);
        var x = new float[Sr * 2];
        int onset = Sr / 2;
        Array.Copy(vowel, 0, x, onset, vowel.Length);
        var sliders = new VoiceSliders { Body = -0.6 };
        var y = AudioEdit.ApplyVoice(x, sliders);
        Assert.Equal(x.Length, y.Length);
        Assert.True(Signals.AllFinite(y));
        Assert.True(Signals.Rms(Signals.Slice(y, 0, 0.45)) < 1e-3, "nothing before the vowel");
        Assert.True(Signals.Rms(Signals.Slice(y, 0.52, 1.05)) > 0.02, "the vowel is there");
        Assert.True(Signals.Rms(Signals.Slice(y, 1.25, 2)) < 1e-2, "nothing long after it");
        int first = Array.FindIndex(y, v => Math.Abs(v) > 0.02f);
        Assert.InRange(first, onset - Sr / 200, onset + Sr / 100); // within 5 ms before / 10 ms after
    }

    [Theory]
    [InlineData("cave")]
    [InlineData("giant")]
    [InlineData("ghost")]
    [InlineData("demon")]
    public void ApplyVoice_KeepsTheTail_AndTrimsTheSilenceAfterIt(string voice)
    {
        var x = Signals.PulseVowel(150, new[] { 700.0, 1200 }, 0.4);
        var sliders = BuiltInVoices.All().First(v => v.Id == voice).Sliders;
        var y = AudioEdit.ApplyVoice(x, sliders);
        Assert.True(y.Length > x.Length + Sr / 5, voice + ": the tail was cut, length " + y.Length);
        Assert.True(Signals.Rms(Signals.Slice(y, 0.4, 0.6)) > 1e-3, voice + ": nothing rings on after the sound");
        Assert.True(y.Length < x.Length + 8 * Sr, voice + ": the silence after the tail was not trimmed");
        double end = y.Length / (double)Sr;
        Assert.True(Signals.Peak(Signals.Slice(y, end - 0.02, end)) < 0.01, voice + ": it stops abruptly");
    }

    [Fact]
    public void ApplyVoiceRange_RingsOnOverWhatFollows()
    {
        var x = new float[Sr * 6];
        var vowel = Signals.PulseVowel(150, new[] { 700.0, 1200 }, 0.3);
        Array.Copy(vowel, x, vowel.Length);
        var cave = BuiltInVoices.All().First(v => v.Id == "cave").Sliders;
        var y = AudioEdit.ApplyVoiceRange(x, 0, vowel.Length, cave);
        Assert.Equal(x.Length, y.Length); // the tail fits in the sound: mixed in, nothing pushed later
        Assert.True(Signals.Rms(Signals.Slice(y, 0.35, 0.9)) > 1e-3, "the tail rings on over what follows");
        Assert.Equal(x, AudioEdit.ApplyVoiceRange(x, Sr * 5, Sr / 2, new VoiceSliders())); // Normal changes nothing
    }

    [Fact]
    public void ApplyVoice_Normal_IsTheSameSound()
    {
        var x = Signals.Sine(300, 0.5);
        Assert.Equal(x, AudioEdit.ApplyVoice(x, new VoiceSliders()));
    }

    // ---------- silence ----------

    [Fact]
    public void RemoveSilence_TrimsBothEnds_KeepingASmallMargin()
    {
        var tone = Signals.Sine(440, 0.5);
        var x = new float[Sr * 2];
        Array.Copy(tone, 0, x, Sr / 2, tone.Length);
        var y = AudioEdit.RemoveSilence(x);
        int margin = Sr / 100;
        Assert.InRange(y.Length, tone.Length, tone.Length + 2 * margin + 2);
        Assert.InRange(Signals.Peak(y), 0.49, 0.51);
    }

    [Fact]
    public void RemoveSilence_AllSilence_KeepsTheSound()
    {
        var x = new float[1000];
        Assert.Equal(1000, AudioEdit.RemoveSilence(x).Length);
        Assert.Equal((0, 0), AudioEdit.SoundBounds(x));
    }

    [Fact]
    public void Clamped_LimitsToFullScale_AndDropsNaN()
    {
        Assert.Equal(new float[] { 1, -1, 0.5f, 0 }, AudioEdit.Clamped(new[] { 2f, -3f, 0.5f, float.NaN }));
    }

    [Fact]
    public void Operations_NeverChangeTheirInput()
    {
        var x = Signals.Sine(300, 0.3);
        var copy = (float[])x.Clone();
        AudioEdit.Reverse(x); AudioEdit.FadeIn(x); AudioEdit.FadeOut(x); AudioEdit.Gain(x, 2); AudioEdit.Normalize(x);
        AudioEdit.ChangeSpeed(x, 1.5, true); AudioEdit.ChangeSpeed(x, 1.5, false); AudioEdit.ShiftPitch(x, 3);
        AudioEdit.Echo(x, AudioEdit.EchoStrength.Strong); AudioEdit.RemoveSilence(x); AudioEdit.Cut(x, 10, 10);
        Assert.Equal(copy, x);
    }
}
