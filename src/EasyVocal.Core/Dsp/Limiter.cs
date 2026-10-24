using System;

namespace EasyVocal.Core.Dsp;

/// <summary>
/// Look-ahead peak limiter: the output never goes above <c>ceiling</c> (a hair of float rounding aside) and a
/// signal below it passes unchanged, only delayed by <see cref="LatencySamples"/> (1.5 ms).
/// <para>
/// Each sample asks for a gain (<c>ceiling / |x|</c> when it is too loud, 1 otherwise). The gain that leaves the
/// limiter is the average over the look-ahead window of the window's minimum, so it is already down (in a short
/// linear ramp) when the loud sample comes out of the delay line; afterwards it climbs back over about 80 ms.
/// All buffers are allocated in the constructor; <see cref="Process"/> never allocates or locks.
/// </para>
/// </summary>
public sealed class Limiter
{
    private readonly float ceiling;
    private readonly int look;           // look-ahead in samples
    private readonly float[] delay;      // the audio, <look> samples late
    private int delayPos;
    private readonly int[] minIdx;       // monotonic deque: indices of rising wanted gains
    private readonly float[] minVal;
    private int dqHead, dqCount;
    private int n;                       // sample counter (wraps; only differences are used)
    private readonly float[] mins;       // window minimum of the last <look + 1> samples (ring)
    private int minsPos;
    private double sum;                  // running sum of <mins>
    private readonly float releaseUp;    // per-sample one-pole step back towards unity gain
    private float gain = 1f;

    public Limiter(int sampleRate, float ceiling = 0.98f)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.ceiling = ceiling > 0.01f ? ceiling : 0.01f;
        look = Math.Max(1, (int)Math.Round(sampleRate * 0.0015));
        int w = look + 1;
        delay = new float[look];
        mins = new float[w];
        int cap = 1;
        while (cap < w + 1) cap <<= 1;
        minIdx = new int[cap];
        minVal = new float[cap];
        releaseUp = (float)(1 - Math.Exp(-1.0 / (0.08 * sampleRate)));
        Reset();
    }

    /// <summary>Fixed delay from input to output (look-ahead; at most 2 ms).</summary>
    public int LatencySamples => look;

    public float Ceiling => ceiling;

    public void Reset()
    {
        Array.Clear(delay);
        Array.Fill(mins, 1f);
        delayPos = minsPos = 0;
        dqHead = dqCount = 0;
        n = 0;
        sum = mins.Length;
        gain = 1f;
    }

    public void Process(Span<float> x)
    {
        int mask = minIdx.Length - 1;
        int w = look + 1;
        for (int i = 0; i < x.Length; i++)
        {
            float v = x[i];
            if (!float.IsFinite(v)) v = 0;
            float a = v < 0 ? -v : v;
            float want = a > ceiling ? ceiling / a : 1f;

            // Sliding minimum of the wanted gain over the last <w> samples.
            while (dqCount > 0 && minVal[(dqHead + dqCount - 1) & mask] >= want) dqCount--;
            int tail = (dqHead + dqCount) & mask;
            minIdx[tail] = n;
            minVal[tail] = want;
            dqCount++;
            while (dqCount > 0 && unchecked(n - minIdx[dqHead]) >= w) { dqHead = (dqHead + 1) & mask; dqCount--; }
            float windowMin = minVal[dqHead];
            n++;

            // Average of that minimum over the last <w> samples: the ramp.
            sum += windowMin - mins[minsPos];
            mins[minsPos] = windowMin;
            if (++minsPos == w) minsPos = 0;
            float target = (float)(sum / w);
            if (target > 1f) target = 1f;

            // Down at once (the ramp is already gradual), back up slowly.
            gain = target < gain ? target : gain + releaseUp * (target - gain);

            float delayed = delay[delayPos];
            delay[delayPos] = v;
            if (++delayPos == delay.Length) delayPos = 0;

            float y = delayed * gain;
            x[i] = y > ceiling ? ceiling : y < -ceiling ? -ceiling : y;
        }
    }
}
