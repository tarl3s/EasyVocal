using EasyVocal.Ai.Dsp;
using EasyVocal.Ai.Rvc;

namespace EasyVocal.Ai.Tests.Helpers;

/// <summary>
/// Numbers for "who is speaking" and "what pitch", so the AI voices can be tested without listening:
/// a timbre print (mean and spread of 19 MFCCs over the louder frames, the classic speaker features: they follow the
/// vocal tract, not the words) and a YIN pitch contour.
/// </summary>
public static class VoiceMeasure
{
    private const int Sr = 16000, NFft = 512, Hop = 160, Win = 400, Bands = 40, Ceps = 20;

    /// <summary>The timbre print of 48 kHz audio (38 numbers).</summary>
    public static double[] Timbre(float[] x48)
    {
        var x = PolyResampler.Convert(x48, 48000, Sr);
        var fft = new Fft(NFft);
        var re = new float[NFft];
        var im = new float[NFft];
        var fb = MelBank();
        var frames = new List<double[]>();
        var energy = new List<double>();
        for (int s = 0; s + Win <= x.Length; s += Hop)
        {
            double e = 0;
            for (int i = 0; i < NFft; i++)
            {
                float v = i < Win ? x[s + i] * (float)(0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (Win - 1))) : 0;
                re[i] = v; im[i] = 0;
                e += v * v;
            }
            fft.Forward(re, im);
            var mel = new double[Bands];
            for (int b = 0; b < Bands; b++)
            {
                double sum = 0;
                for (int k = 0; k <= NFft / 2; k++) sum += fb[b, k] * (re[k] * re[k] + im[k] * im[k]);
                mel[b] = Math.Log(sum + 1e-10);
            }
            var c = new double[Ceps - 1];
            for (int q = 1; q < Ceps; q++)
            {
                double sum = 0;
                for (int b = 0; b < Bands; b++) sum += mel[b] * Math.Cos(Math.PI * q * (b + 0.5) / Bands);
                c[q - 1] = sum;
            }
            frames.Add(c);
            energy.Add(10 * Math.Log10(e + 1e-12));
        }
        double top = energy.Count > 0 ? energy.Max() : 0;
        var keep = frames.Where((_, i) => energy[i] > top - 30).ToList();
        if (keep.Count == 0) keep = frames;
        int d = Ceps - 1;
        var print = new double[2 * d];
        for (int j = 0; j < d; j++)
        {
            double m = keep.Average(f => f[j]);
            double sd = Math.Sqrt(keep.Average(f => (f[j] - m) * (f[j] - m)));
            print[j] = m;
            print[d + j] = sd;
        }
        return print;
    }

    /// <summary>Euclidean distance between two prints (smaller = more alike).</summary>
    public static double Distance(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += (a[i] - b[i]) * (a[i] - b[i]);
        return Math.Sqrt(s);
    }

    /// <summary>YIN pitch per 10 ms of 48 kHz audio (0 = unvoiced).</summary>
    public static float[] Pitch(float[] x48)
    {
        var x = PolyResampler.Convert(x48, 48000, Sr);
        var f0 = new float[1 + x.Length / 160];
        new Yin().Estimate(x, f0);
        return f0;
    }

    /// <summary>
    /// How the output's pitch relates to the input's over the frames where both are voiced (after the best lag up to
    /// 0.6 s): the median ratio in semitones, the median deviation from it in cents, and the share of frames compared.
    /// </summary>
    public static (double Semitones, double SpreadCents, double Voiced) PitchFollow(float[] input48, float[] output48)
    {
        var a = Pitch(input48);
        var b = Pitch(output48);
        (double, double, double) best = (0, double.MaxValue, 0);
        for (int lag = 0; lag <= 60; lag++)
        {
            var r = new List<double>();
            int both = 0, voicedIn = 0;
            for (int i = 0; i + lag < b.Length && i < a.Length; i++)
            {
                if (a[i] > 0) voicedIn++;
                if (a[i] > 0 && b[i + lag] > 0) { both++; r.Add(12 * Math.Log2(b[i + lag] / a[i])); }
            }
            if (r.Count < 20) continue;
            r.Sort();
            double med = r[r.Count / 2];
            var dev = r.Select(v => Math.Abs(v - med) * 100).OrderBy(v => v).ToList();
            double spread = dev[dev.Count / 2];
            if (spread < best.Item2) best = (med, spread, voicedIn > 0 ? (double)both / voicedIn : 0);
        }
        return best;
    }

    /// <summary>Share of 10 ms frames whose high-band energy jumps 12 dB above both neighbours (clicks at block seams).</summary>
    public static double Clicks(float[] x48, int from = 0)
    {
        int frame = 480;
        var e = new List<double>();
        float prev = 0;
        for (int s = from; s + frame <= x48.Length; s += frame)
        {
            double sum = 0;
            for (int i = s; i < s + frame; i++)
            {
                float hp = x48[i] - 0.97f * prev;
                prev = x48[i];
                sum += hp * hp;
            }
            e.Add(10 * Math.Log10(sum + 1e-9));
        }
        int clicks = 0;
        for (int i = 1; i + 1 < e.Count; i++) if (e[i] - Math.Max(e[i - 1], e[i + 1]) > 12) clicks++;
        return e.Count > 2 ? (double)clicks / (e.Count - 2) : 0;
    }

    private static double[,] MelBank()
    {
        static double Mel(double f) => 2595 * Math.Log10(1 + f / 700);
        static double Hz(double m) => 700 * (Math.Pow(10, m / 2595) - 1);
        var fb = new double[Bands, NFft / 2 + 1];
        double lo = Mel(60), hi = Mel(7600);
        var pts = Enumerable.Range(0, Bands + 2).Select(i => Hz(lo + (hi - lo) * i / (Bands + 1))).ToArray();
        for (int b = 0; b < Bands; b++)
            for (int k = 0; k <= NFft / 2; k++)
            {
                double f = k * (double)Sr / NFft;
                double w = Math.Max(0, Math.Min((f - pts[b]) / (pts[b + 1] - pts[b]), (pts[b + 2] - f) / (pts[b + 2] - pts[b + 1])));
                fb[b, k] = w;
            }
        return fb;
    }
}
