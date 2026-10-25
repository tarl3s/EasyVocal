using System;
using EasyVocal.Core.Sounds;

namespace EasyVocal.Tests;

public class WavePeaksTests
{
    private static (float, float) Scan(float[] x, long a, long b)
    {
        a = Math.Max(0, a);
        b = Math.Min(x.Length, b);
        if (a >= b) return (1, -1);
        float lo = float.MaxValue, hi = float.MinValue;
        for (long i = a; i < b; i++)
        {
            lo = Math.Min(lo, x[i]);
            hi = Math.Max(hi, x[i]);
        }
        return (lo, hi);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(1000)]
    [InlineData(100_003)]
    public void MinMax_IsWhatAScanOfTheRangeGives(int length)
    {
        var rng = new Random(length);
        var x = new float[length];
        for (int i = 0; i < length; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        var peaks = new WavePeaks(x);
        Assert.Same(x, peaks.Samples);
        for (int k = 0; k < 2000; k++)
        {
            long a = rng.Next(-10, length + 10), b = a + rng.Next(0, Math.Max(1, length / (1 + k % 7)) + 10);
            Assert.Equal(Scan(x, a, b), peaks.MinMax(a, b));
        }
        Assert.Equal(Scan(x, 0, length), peaks.MinMax(0, length));
        Assert.Equal((1f, -1f), peaks.MinMax(5, 5)); // empty
    }

    [Fact]
    public void MinMax_FindsASingleSpikeInALongSound()
    {
        var x = new float[10 * 60 * 48000]; // 10 minutes
        x[12_345_677] = 0.9f;
        x[20_000_001] = -0.7f;
        var peaks = new WavePeaks(x);
        Assert.Equal((0f, 0.9f), peaks.MinMax(1_000, 15_000_000));
        Assert.Equal((-0.7f, 0f), peaks.MinMax(15_000_000, x.Length));
        Assert.Equal((-0.7f, 0.9f), peaks.MinMax(0, x.Length));
    }
}
