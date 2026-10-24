using System.IO;
using EasyVocal.Core.Dsp;

namespace EasyVocal.E2E;

/// <summary>The e2e test's voice and the measurements it makes on what the devices recorded (mono 48 kHz).</summary>
internal static class E2ESignal
{
    private const int Sr = AudioFormat.SampleRate;

    public const double F0 = 150;

    /// <summary>The voice loop: <see cref="VoicedSeconds"/> of a vowel, then a gap of silence.</summary>
    public const double LoopSeconds = 1.2;

    public const double VoicedSeconds = 1.0;

    /// <summary>
    /// A 150 Hz vowel: an impulse train through three formant resonators ("ah"), peak 0.5, faded in and out over
    /// 10 ms, then 0.2 s of silence. A whole number of periods, so the loop repeats without a seam.
    /// </summary>
    public static float[] Voice()
    {
        int voiced = (int)(VoicedSeconds * Sr), len = (int)(LoopSeconds * Sr), period = (int)(Sr / F0);
        var x = new double[voiced];
        for (int n = 0; n < voiced; n += period) x[n] = 1;
        foreach (double f in new[] { 700.0, 1220, 2600 })
        {
            double bw = 90 + f * 0.05, r = Math.Exp(-Math.PI * bw / Sr);
            double a1 = 2 * r * Math.Cos(2 * Math.PI * f / Sr), a2 = -r * r, y1 = 0, y2 = 0;
            for (int n = 0; n < voiced; n++)
            {
                double y = x[n] + a1 * y1 + a2 * y2;
                y2 = y1; y1 = y; x[n] = y;
            }
        }
        double peak = 0;
        foreach (double v in x) peak = Math.Max(peak, Math.Abs(v));
        int fade = Sr / 100;
        var o = new float[len];
        for (int n = 0; n < voiced; n++)
        {
            double g = Math.Min(1, Math.Min(n, voiced - 1 - n) / (double)fade);
            o[n] = (float)(x[n] / peak * 0.5 * g);
        }
        return o;
    }

    /// <summary>A mono 16-bit 48 kHz WAV file of a sine (what the person would pick as a custom sound).</summary>
    public static void WriteToneWav(string path, double hz, double seconds, double amp)
    {
        int n = (int)(seconds * Sr);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + n * 2);
        w.Write("WAVEfmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);       // PCM
        w.Write((short)1);       // mono
        w.Write(Sr);
        w.Write(Sr * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(n * 2);
        int fade = Sr / 200;
        for (int i = 0; i < n; i++)
        {
            double g = Math.Min(1, Math.Min(i, n - 1 - i) / (double)fade);
            w.Write((short)Math.Round(amp * g * Math.Sin(2 * Math.PI * hz * i / Sr) * 32767));
        }
    }

    public static double Rms(float[] x)
    {
        double s = 0;
        foreach (float v in x) s += (double)v * v;
        return x.Length == 0 ? 0 : Math.Sqrt(s / x.Length);
    }

    public static double MaxAbsDiff(float[] a, float[] b)
    {
        double m = 0;
        for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
        return m;
    }

    public static float[] Minus(float[] a, float[] b)
    {
        var o = new float[a.Length];
        for (int i = 0; i < a.Length; i++) o[i] = a[i] - b[i];
        return o;
    }

    /// <summary>Normalised correlation of two equally long signals (1 = the same shape, 0 = unrelated).</summary>
    public static double Correlation(float[] a, float[] b)
    {
        double ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++) { ab += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        double d = Math.Sqrt(aa * bb);
        return d < 1e-20 ? 0 : ab / d;
    }

    /// <summary>Amplitude of the component at <paramref name="hz"/> (Goertzel; a sine of amplitude A gives about A).</summary>
    public static double Magnitude(float[] x, double hz)
    {
        double w = 2 * Math.PI * hz / Sr, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        foreach (float v in x)
        {
            double s = v + c * s1 - s2;
            s2 = s1; s1 = s;
        }
        double re = s1 - s2 * Math.Cos(w), im = s2 * Math.Sin(w);
        return x.Length == 0 ? 0 : 2 * Math.Sqrt(re * re + im * im) / x.Length;
    }

    /// <summary>Share of the energy that sits on the harmonics of <see cref="F0"/> (about 1 for the clean vowel).</summary>
    public static double HarmonicShare(float[] x)
    {
        double total = 0;
        foreach (float v in x) total += (double)v * v;
        total /= Math.Max(1, x.Length);
        if (total < 1e-12) return 0;
        double onHarmonics = 0;
        for (int k = 1; k * F0 < 4000; k++)
        {
            double a = Magnitude(x, k * F0);
            onHarmonics += a * a / 2;
        }
        return onHarmonics / total;
    }

    /// <summary>Pitch in Hz by autocorrelation (60-1000 Hz); 0 when nothing periodic is found.</summary>
    public static double Pitch(float[] x)
    {
        int minLag = Sr / 1000, maxLag = Sr / 60;
        if (x.Length < maxLag * 3) return 0;
        var r = new double[maxLag + 2];
        double best = 0;
        for (int lag = minLag; lag <= maxLag + 1; lag++)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int n = 0; n + lag < x.Length; n++)
            {
                double a = x[n], b = x[n + lag];
                xy += a * b; xx += a * a; yy += b * b;
            }
            double d = Math.Sqrt(xx * yy);
            r[lag] = d < 1e-20 ? 0 : xy / d;
            if (lag <= maxLag && r[lag] > best) best = r[lag];
        }
        if (best < 0.2) return 0;
        for (int lag = minLag + 1; lag <= maxLag; lag++)
            if (r[lag] >= 0.9 * best && r[lag] >= r[lag - 1] && r[lag] >= r[lag + 1])
                return (double)Sr / lag;
        return 0;
    }
}
