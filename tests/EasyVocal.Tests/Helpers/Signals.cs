using System;
using EasyVocal.Core.Dsp;

namespace EasyVocal.Tests.Helpers;

/// <summary>Test signals and measurements (mono float at <see cref="AudioFormat.SampleRate"/>).</summary>
public static class Signals
{
    private const int Sr = AudioFormat.SampleRate;

    private static int Count(double seconds) => (int)Math.Round(seconds * Sr);

    public static float[] Silence(double seconds) => new float[Count(seconds)];

    public static float[] Sine(double hz, double seconds, float amp = 0.5f)
    {
        var x = new float[Count(seconds)];
        for (int n = 0; n < x.Length; n++)
            x[n] = (float)(amp * Math.Sin(2 * Math.PI * hz * n / Sr));
        return x;
    }

    /// <summary>White uniform noise in [-amp, amp], deterministic for a seed.</summary>
    public static float[] Noise(double seconds, float amp, int seed)
    {
        var rng = new Random(seed);
        var x = new float[Count(seconds)];
        for (int n = 0; n < x.Length; n++)
            x[n] = (float)((rng.NextDouble() * 2 - 1) * amp);
        return x;
    }

    /// <summary>Impulse train at f0 through one 2-pole resonator per formant (cascade), peak-normalised to 0.5.</summary>
    public static float[] PulseVowel(double f0, double[] formants, double seconds)
    {
        int len = Count(seconds);
        var x = new double[len];
        double period = Sr / f0, next = 0;
        for (int n = 0; n < len; n++)
        {
            if (n >= next) { x[n] = 1; next += period; }
        }
        foreach (double f in formants)
        {
            double bw = 90 + f * 0.05;
            double r = Math.Exp(-Math.PI * bw / Sr);
            double a1 = 2 * r * Math.Cos(2 * Math.PI * f / Sr), a2 = -r * r;
            double y1 = 0, y2 = 0;
            for (int n = 0; n < len; n++)
            {
                double y = x[n] + a1 * y1 + a2 * y2;
                y2 = y1; y1 = y; x[n] = y;
            }
        }
        double peak = 0;
        for (int n = 0; n < len; n++) peak = Math.Max(peak, Math.Abs(x[n]));
        var o = new float[len];
        if (peak > 0)
            for (int n = 0; n < len; n++) o[n] = (float)(x[n] / peak * 0.5);
        return o;
    }

    /// <summary>Amplitude of the component at <paramref name="hz"/> (Goertzel; a sine of amplitude A gives about A).</summary>
    public static double Magnitude(float[] x, double hz)
    {
        if (x.Length == 0) return 0;
        double w = 2 * Math.PI * hz / Sr, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int n = 0; n < x.Length; n++)
        {
            double s = x[n] + c * s1 - s2;
            s2 = s1; s1 = s;
        }
        double re = s1 - s2 * Math.Cos(w), im = s2 * Math.Sin(w);
        return 2 * Math.Sqrt(re * re + im * im) / x.Length;
    }

    /// <summary>Normalised autocorrelation at one lag (1 = perfectly periodic at that lag).</summary>
    public static double Autocorr(float[] x, int lag)
    {
        double xy = 0, xx = 0, yy = 0;
        for (int n = 0; n + lag < x.Length; n++)
        {
            double a = x[n], b = x[n + lag];
            xy += a * b; xx += a * a; yy += b * b;
        }
        double d = Math.Sqrt(xx * yy);
        return d < 1e-20 ? 0 : xy / d;
    }

    /// <summary>Pitch in Hz by autocorrelation, searching 60-1000 Hz; 0 when nothing periodic is found.</summary>
    public static double EstimatePitch(float[] x)
    {
        int minLag = Sr / 1000, maxLag = Sr / 60;
        if (x.Length < maxLag * 3) return 0;
        int start = x.Length / 4;
        var seg = new float[Math.Min(Sr / 2, x.Length - start)];
        Array.Copy(x, start, seg, 0, seg.Length);
        var r = new double[maxLag + 2];
        double best = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            r[lag] = Autocorr(seg, lag);
            if (r[lag] > best) best = r[lag];
        }
        if (best < 0.2) return 0;
        for (int lag = minLag + 1; lag <= maxLag; lag++)
            if (r[lag] >= 0.9 * best && r[lag] >= r[lag - 1] && r[lag] >= r[lag + 1])
                return (double)Sr / lag;
        return 0;
    }

    /// <summary>Spectral centroid (Hz) of the energy between lo and hi (Hann window, power spectrum).</summary>
    public static double Centroid(float[] x, double lo, double hi)
    {
        int n = 1;
        while (n < x.Length) n <<= 1;
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < x.Length; i++)
            re[i] = x[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Math.Max(1, x.Length - 1)));
        Fft(re, im);
        double num = 0, den = 0;
        for (int k = 1; k < n / 2; k++)
        {
            double f = (double)k * Sr / n;
            if (f < lo || f > hi) continue;
            double p = re[k] * re[k] + im[k] * im[k];
            num += f * p; den += p;
        }
        return den <= 0 ? 0 : num / den;
    }

    /// <summary>Largest absolute value; NaN when any sample is NaN.</summary>
    public static double Peak(float[] x)
    {
        double p = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double a = Math.Abs(x[i]);
            if (double.IsNaN(a)) return double.NaN;
            if (a > p) p = a;
        }
        return p;
    }

    public static double Rms(float[] x)
    {
        if (x.Length == 0) return 0;
        double s = 0;
        for (int i = 0; i < x.Length; i++) s += (double)x[i] * x[i];
        return Math.Sqrt(s / x.Length);
    }

    /// <summary>Copy of the part between two times (seconds).</summary>
    public static float[] Slice(float[] x, double fromSec, double toSec)
    {
        int a = Math.Clamp(Count(fromSec), 0, x.Length), b = Math.Clamp(Count(toSec), a, x.Length);
        var o = new float[b - a];
        Array.Copy(x, a, o, 0, o.Length);
        return o;
    }

    public static bool AllFinite(float[] x)
    {
        for (int i = 0; i < x.Length; i++)
            if (!float.IsFinite(x[i])) return false;
        return true;
    }

    /// <summary>Runs an effect over a signal in <see cref="AudioFormat.BlockSize"/> blocks, returning a processed copy.</summary>
    public static float[] Run(IAudioEffect fx, float[] input)
    {
        var o = (float[])input.Clone();
        for (int i = 0; i < o.Length; i += AudioFormat.BlockSize)
            fx.Process(o.AsSpan(i, Math.Min(AudioFormat.BlockSize, o.Length - i)));
        return o;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }
}
