using System;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

/// <summary>The output queue that absorbs the clock drift between the mic and a playback device.</summary>
public class SampleQueueTests
{
    private const int Sr = AudioFormat.SampleRate;
    private const int Fade = Sr * 5 / 1000;

    private static SampleQueue NewQueue(int prime = 0) => new(Sr / 2, Fade, prime);

    private static float[] ReadAll(SampleQueue q, int total, int chunk = 480)
    {
        var o = new float[total];
        for (int i = 0; i < total; i += chunk) q.Read(o.AsSpan(i, Math.Min(chunk, total - i)));
        return o;
    }

    private static double MaxStep(float[] x)
    {
        double m = 0;
        for (int n = 1; n < x.Length; n++) m = Math.Max(m, Math.Abs(x[n] - x[n - 1]));
        return m;
    }

    [Fact]
    public void WriteThenRead_SameSamples()
    {
        var q = NewQueue();
        var x = Signals.Sine(300, 0.05, 0.5f);
        q.Write(x);
        Assert.Equal(x.Length, q.Count);
        var o = new float[x.Length];
        Assert.Equal(x.Length, q.Read(o));
        Assert.Equal(x, o);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void TrimTo_KeepsTheTargetFill_AndCrossfadesTheCut()
    {
        var q = NewQueue();
        // 100 Hz: the 3040 samples dropped are 6.33 periods, so a hard cut would jump by about 0.8.
        var x = Signals.Sine(100, 4000.0 / Sr, 0.5f);
        q.Write(x);
        q.TrimTo(960);
        Assert.Equal(960, q.Count);

        var o = ReadAll(q, 960);
        Assert.True(Signals.AllFinite(o));
        Assert.True(MaxStep(o) < 0.03, "step at the cut " + MaxStep(o));
        // After the crossfade it is exactly the newest audio.
        for (int n = Fade; n < 960; n++) Assert.Equal(x[4000 - 960 + n], o[n], 1e-6f);
        // It starts where playback was (the oldest sample), so nothing jumps at the start either.
        Assert.True(Math.Abs(o[0] - x[0]) < 0.03);
    }

    [Fact]
    public void TrimTo_WhenAlreadyBelow_DoesNothing()
    {
        var q = NewQueue();
        var x = Signals.Sine(300, 0.01, 0.5f);
        q.Write(x);
        q.TrimTo(960);
        Assert.Equal(x.Length, q.Count);
        Assert.Equal(x, ReadAll(q, x.Length));
    }

    [Fact]
    public void TrimTo_SmallDrop_StillSmooth()
    {
        var q = NewQueue();
        var x = Signals.Sine(100, 1200.0 / Sr, 0.5f);
        q.Write(x);
        q.TrimTo(1100); // drops fewer samples than the crossfade is long
        Assert.Equal(1100, q.Count);
        var o = ReadAll(q, 1100);
        Assert.True(MaxStep(o) < 0.03, "step " + MaxStep(o));
    }

    [Fact]
    public void DrySpell_FadesOut_WaitsForACushion_ThenFadesIn()
    {
        var q = NewQueue(prime: 480);
        q.Write(Constant(600, 0.5f));
        var o = new float[480];
        Assert.Equal(480, q.Read(o));
        Assert.All(o, v => Assert.Equal(0.5f, v));
        Assert.Equal(120, q.Read(o)); // the device asks for more than there is
        Assert.Equal(0f, o[120]); // silence after what there was
        Assert.True(o[119] < 0.01f, "the tail fades out instead of stopping dead: " + o[119]);
        Assert.True(MaxStep(o) < 0.01, "step " + MaxStep(o));

        q.Write(Constant(200, 0.5f)); // less than the cushion: wait
        Assert.Equal(0, q.Read(o));
        q.Write(Constant(400, 0.5f));
        Assert.Equal(480, q.Read(o));
        Assert.True(o[0] < 0.01f, "fades back in: " + o[0]);
        Assert.Equal(0.5f, o[Fade + 1], 1e-6f);
        Assert.True(MaxStep(o) < 0.01);
    }

    [Fact]
    public void ShortTail_BelowTheCushion_StillPlays()
    {
        var q = NewQueue(prime: 480);
        q.Write(Constant(100, 0.5f)); // the very end of a sound
        var o = new float[480];
        int got = 0;
        for (int i = 0; i < 4 && got == 0; i++) got = q.Read(o);
        Assert.Equal(100, got);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Overflow_DropsWhatDoesNotFit()
    {
        var q = new SampleQueue(1000, Fade, 0);
        q.Write(new float[800]);
        q.Write(new float[800]);
        Assert.Equal(1000, q.Count);
    }

    private static float[] Constant(int n, float v)
    {
        var x = new float[n];
        Array.Fill(x, v);
        return x;
    }
}
