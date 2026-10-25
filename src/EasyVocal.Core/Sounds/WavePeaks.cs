using System;

namespace EasyVocal.Core.Sounds;

/// <summary>
/// The quietest and loudest sample of a sound over any range, fast: a min/max pyramid (blocks of
/// <see cref="BaseBlock"/> samples, then of twice as many at every level up) built once per sound. A range reads
/// the biggest whole blocks that fit in it and only the few samples at its edges one by one, so drawing a 10-minute
/// sound zoomed out costs a few thousand reads instead of a scan of every sample.
/// </summary>
public sealed class WavePeaks
{
    /// <summary>Samples per block at the finest level.</summary>
    public const int BaseBlock = 64;

    private readonly float[] samples;
    private readonly float[][] mins, maxs; // [level][block]; level L holds blocks of BaseBlock << L samples

    /// <summary>Builds the pyramid (one pass over the sound; call it off the UI thread for a long one).</summary>
    public WavePeaks(float[] samples)
    {
        this.samples = samples ?? throw new ArgumentNullException(nameof(samples));
        int levels = 0;
        for (long n = samples.Length / BaseBlock; n >= 1; n /= 2) levels++;
        mins = new float[levels][];
        maxs = new float[levels][];
        if (levels == 0) return;

        int count = samples.Length / BaseBlock; // whole blocks only: the tail is read sample by sample
        var lo = new float[count];
        var hi = new float[count];
        for (int b = 0; b < count; b++)
        {
            int at = b * BaseBlock;
            float l = samples[at], h = l;
            for (int i = at + 1; i < at + BaseBlock; i++)
            {
                float v = samples[i];
                if (v < l) l = v;
                if (v > h) h = v;
            }
            lo[b] = l;
            hi[b] = h;
        }
        mins[0] = lo;
        maxs[0] = hi;
        for (int level = 1; level < levels; level++)
        {
            var pl = mins[level - 1];
            var ph = maxs[level - 1];
            int n = pl.Length / 2;
            lo = new float[n];
            hi = new float[n];
            for (int b = 0; b < n; b++)
            {
                lo[b] = Math.Min(pl[2 * b], pl[2 * b + 1]);
                hi[b] = Math.Max(ph[2 * b], ph[2 * b + 1]);
            }
            mins[level] = lo;
            maxs[level] = hi;
        }
    }

    /// <summary>The sound the pyramid was built for.</summary>
    public float[] Samples => samples;

    /// <summary>The lowest and highest sample in [<paramref name="from"/>, <paramref name="to"/>) (clamped to the sound); (1, -1) for an empty range.</summary>
    public (float Min, float Max) MinMax(long from, long to)
    {
        long a = Math.Max(0, from), b = Math.Min(samples.Length, to);
        float lo = 1, hi = -1;
        if (a >= b) return (lo, hi);
        lo = float.MaxValue;
        hi = float.MinValue;
        while (a < b)
        {
            if (a % BaseBlock == 0 && a + BaseBlock <= b && mins.Length > 0)
            {
                // The biggest block that starts here and fits.
                long block = a / BaseBlock;
                int level = 0;
                while (level + 1 < mins.Length && (block & ((1L << (level + 1)) - 1)) == 0 &&
                       a + ((long)BaseBlock << (level + 1)) <= b && (block >> (level + 1)) < mins[level + 1].Length)
                    level++;
                long index = block >> level;
                if (index < mins[level].Length)
                {
                    if (mins[level][index] < lo) lo = mins[level][index];
                    if (maxs[level][index] > hi) hi = maxs[level][index];
                    a += (long)BaseBlock << level;
                    continue;
                }
            }
            float v = samples[a++];
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        return (lo, hi);
    }
}
