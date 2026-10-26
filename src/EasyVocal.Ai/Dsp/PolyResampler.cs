using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyVocal.Ai.Dsp;

/// <summary>
/// Streaming rational resampler (polyphase windowed sinc, Kaiser β 8.6, 16 zero crossings): 48 kHz → 16 kHz for the
/// models, the model's 32/40/48 kHz → 48 kHz back. No allocation after construction. Delay: <see cref="DelayIn"/>
/// input samples.
/// </summary>
public sealed class PolyResampler
{
    private readonly int up, down, taps;
    private readonly float[] coef;      // [up][taps], each phase reversed so it dots with the history window
    private readonly float[] hist;      // 2 × taps: every sample written twice, so the window is one span
    private int w;                       // where the newest sample is (0..taps-1)
    private long outCount, inCount;

    public PolyResampler(int inRate, int outRate, int zeroCrossings = 16)
    {
        int g = Gcd(inRate, outRate);
        up = outRate / g;
        down = inRate / g;
        int m = Math.Max(up, down);
        double fc = 0.5 / m * 0.94;                      // cycles per sample at the up-sampled rate
        int n = (int)Math.Ceiling(2.0 * zeroCrossings / (2 * fc));
        taps = (n + up - 1) / up;
        n = taps * up;
        double c = (n - 1) / 2.0, beta = 8.6, i0b = I0(beta);
        var proto = new double[n];
        for (int j = 0; j < n; j++)
        {
            double t = j - c;
            double x = 2 * fc * t;
            double sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            double r = t / (c + 1);
            double win = Math.Abs(r) >= 1 ? 0 : I0(beta * Math.Sqrt(1 - r * r)) / i0b;
            proto[j] = 2 * fc * sinc * win * up;
        }
        coef = new float[up * taps];
        for (int p = 0; p < up; p++)
            for (int k = 0; k < taps; k++)
                coef[p * taps + (taps - 1 - k)] = (float)proto[p + k * up];
        hist = new float[2 * taps];
        DelayIn = (n - 1) / 2.0 / up;
    }

    /// <summary>The filter's delay, in input samples.</summary>
    public double DelayIn { get; }

    public int OutRate => up;

    /// <summary>Most output samples <see cref="Process"/> can give for <paramref name="inputLength"/> input samples.</summary>
    public int MaxOutput(int inputLength) => (int)((long)inputLength * up / down) + 2;

    /// <summary>Feeds input; writes the output that is ready and returns how many samples that was.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int produced = 0;
        for (int i = 0; i < input.Length; i++)
        {
            w = w + 1 == taps ? 0 : w + 1;
            hist[w] = hist[w + taps] = input[i];
            long latest = inCount++;
            while (true)
            {
                long t = outCount * down;
                long b = t / up;
                if (b > latest) break;
                int phase = (int)(t - b * up);
                output[produced++] = Dot(hist.AsSpan(w + 1, taps), coef.AsSpan(phase * taps, taps));
                outCount++;
            }
        }
        return produced;
    }

    /// <summary>Back to silence (as if new).</summary>
    public void Reset()
    {
        Array.Clear(hist);
        w = 0;
        outCount = inCount = 0;
    }

    /// <summary>One whole signal, its length scaled by the ratio and the filter delay taken off.</summary>
    public static float[] Convert(ReadOnlySpan<float> x, int inRate, int outRate)
    {
        if (inRate == outRate) return x.ToArray();
        var r = new PolyResampler(inRate, outRate);
        int delay = (int)Math.Round(r.DelayIn * outRate / inRate);
        int want = (int)Math.Round((long)x.Length * (double)outRate / inRate);
        var buf = new float[r.MaxOutput(x.Length + (int)r.DelayIn * 2 + 8)];
        int n = r.Process(x, buf);
        var pad = new float[(int)Math.Ceiling(r.DelayIn) * 2 + 4];
        n += r.Process(pad, buf.AsSpan(n));
        var o = new float[want];
        for (int i = 0; i < want; i++) o[i] = i + delay < n ? buf[i + delay] : 0;
        return o;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var acc = Vector<float>.Zero;
        for (int i = 0; i < va.Length; i++) acc += va[i] * vb[i];
        float s = Vector.Dot(acc, Vector<float>.One);
        for (int i = va.Length * Vector<float>.Count; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    private static double I0(double x)
    {
        double s = 1, t = 1;
        for (int k = 1; k < 40; k++) { t *= x / (2 * k); s += t * t; }
        return s;
    }
}
