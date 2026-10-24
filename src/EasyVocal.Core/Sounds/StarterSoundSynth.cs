using System;
using System.IO;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using NAudio.Wave;

namespace EasyVocal.Core.Sounds;

/// <summary>
/// Generates the starter soundboard sounds from plain maths (no recordings, so no licences). Every recipe is
/// deterministic and ends with the same clean-up: mean removed, short fades, peak set to the recipe's level.
/// </summary>
public static class StarterSoundSynth
{
    private const int Sr = AudioFormat.SampleRate;
    private const double TwoPi = 2 * Math.PI;

    /// <summary>Writes <c>&lt;id&gt;.wav</c> (48 kHz, 16-bit, mono) for every starter sound, skipping files that exist.</summary>
    public static void WriteAll(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var item in StarterSounds.Items())
        {
            string path = Path.Combine(folder, item.FileName);
            if (File.Exists(path)) continue;
            var samples = Render(item.Id);
            if (samples == null) continue;
            WriteWav(path, samples);
        }
    }

    /// <summary>The finished samples (-1..1) of one starter sound, or null for an unknown id.</summary>
    public static double[]? Render(string id) => id switch
    {
        "airhorn" => Airhorn(),
        "ding" => Ding(),
        "sad-trombone" => SadTrombone(),
        "drum-roll" => DrumRoll(),
        "rimshot" => Rimshot(),
        "crickets" => Crickets(),
        "applause" => Applause(),
        "boing" => Boing(),
        "buzzer" => Buzzer(),
        "tada" => Tada(),
        _ => null,
    };

    // ---------- recipes ----------

    private static double[] Airhorn()
    {
        var b = Buffer(1.4);
        double[] notes = { 440, 554.37, 659.25 };
        double[] detune = { -0.003, 0.0, 0.003 };
        for (int v = 0; v < 3; v++)
        {
            double f0 = notes[v] * (1 + detune[v]);
            AddVoice(b, 0, 1.4,
                t => f0 * (1 + 0.035 * Math.Exp(-t / 0.05)) * (1 + 0.004 * Math.Sin(TwoPi * 5.5 * t) * Smooth(t / 0.3)),
                t => 0.5 * Envelope(t, 1.4, 0.015, 0.16),
                k => 1.0 / Math.Pow(k, 0.85), 7000);
        }
        for (int i = 0; i < b.Length; i++) b[i] = Math.Tanh(1.7 * b[i]); // a bit of blare
        return Finish(b, 0.8);
    }

    private static double[] Ding()
    {
        var b = Buffer(2.0);
        // (frequency, level, decay time)
        double[,] partials = { { 1320, 0.6, 0.45 }, { 2640, 0.25, 0.2 }, { 3540, 0.1, 0.1 } };
        for (int p = 0; p < partials.GetLength(0); p++)
        {
            double f = partials[p, 0], a = partials[p, 1], tau = partials[p, 2];
            for (int i = 0; i < b.Length; i++)
            {
                double t = (double)i / Sr;
                b[i] += a * Math.Sin(TwoPi * f * t) * Math.Exp(-t / tau) * Math.Min(1, t / 0.001);
            }
        }
        return Finish(b, 0.6);
    }

    private static double[] SadTrombone()
    {
        var b = Buffer(3.1);
        double[] freq = { 233.08, 220.0, 207.65 };
        for (int n = 0; n < 3; n++)
        {
            double f0 = freq[n], start = n * 0.55;
            AddVoice(b, start, 0.52, t => f0, t => 0.8 * Envelope(t, 0.52, 0.05, 0.09), Brass, 6000);
        }
        // The last note slides down and wobbles more and more.
        AddVoice(b, 1.65, 1.4,
            t => 196.0 * Math.Pow(2, -3.0 / 12 * Smooth(t / 1.4)) * (1 + 0.02 * Smooth(t / 0.7) * Math.Sin(TwoPi * 6 * t)),
            t => 0.8 * Envelope(t, 1.4, 0.06, 0.25), Brass, 6000);
        return Finish(b, 0.7);
    }

    private static double[] DrumRoll()
    {
        var b = Buffer(2.45);
        var rng = new Random(2);
        const int hits = 50;
        for (int i = 0; i < hits; i++)
        {
            double crescendo = 0.2 + 0.8 * Math.Pow((double)i / hits, 1.2);
            double amp = crescendo * (0.85 + 0.15 * rng.NextDouble());
            double t0 = i * 0.04 + (rng.NextDouble() - 0.5) * 0.003;
            Drumstroke(b, Math.Max(0, t0), amp, i % 2 == 0 ? 175 : 195, 0.018, rng);
        }
        Drumstroke(b, 2.02, 1.6, 140, 0.09, rng); // the final accent
        return Finish(b, 0.7);
    }

    private static double[] Rimshot()
    {
        var b = Buffer(1.2);
        var rng = new Random(3);
        Tom(b, 0.0, 230, 135, 0.9, rng);
        Tom(b, 0.2, 175, 100, 1.0, rng);
        // "tss": high-passed noise with a quick decay
        double lp = 0, a = 1 - Math.Exp(-TwoPi * 4500 / Sr);
        int i0 = (int)(0.5 * Sr);
        for (int i = 0; i0 + i < b.Length; i++)
        {
            double t = (double)i / Sr;
            double x = rng.NextDouble() * 2 - 1;
            lp += a * (x - lp);
            double env = Math.Min(1, t / 0.002) * (0.75 * Math.Exp(-t / 0.07) + 0.25 * Math.Exp(-t / 0.22));
            b[i0 + i] += 0.8 * (x - lp) * env;
        }
        return Finish(b, 0.7);
    }

    private static double[] Crickets()
    {
        var b = Buffer(2.7);
        AddChirps(b, 0.05, 4500, 0.34, 0.5);
        AddChirps(b, 0.17, 4350, 0.39, 0.3);
        return Finish(b, 0.5);
    }

    private static double[] Applause()
    {
        const double length = 3.4;
        var b = Buffer(length);
        var rng = new Random(5);
        double lowA = 1 - Math.Exp(-TwoPi * 800 / Sr), highA = 1 - Math.Exp(-TwoPi * 3500 / Sr);
        var clap = new double[900];
        for (int i = 0; i < b.Length; i++)
        {
            double t = (double)i / Sr;
            double density = 450 * Smooth(t / 0.9) * (1 - Smooth((t - 2.2) / (length - 2.2)));
            if (rng.NextDouble() < density / Sr)
            {
                // one clap: band-passed noise burst
                double amp = 0.3 + 0.7 * rng.NextDouble(), tau = 0.003 + 0.006 * rng.NextDouble();
                double lo = 0, hi = 0;
                for (int k = 0; k < clap.Length && i + k < b.Length; k++)
                {
                    double x = rng.NextDouble() * 2 - 1;
                    lo += lowA * (x - lo);
                    hi += highA * (x - hi);
                    b[i + k] += amp * (hi - lo) * Math.Exp(-k / (tau * Sr));
                }
            }
        }
        // a soft bed of many distant claps
        double bl = 0, bh = 0;
        for (int i = 0; i < b.Length; i++)
        {
            double t = (double)i / Sr;
            double x = rng.NextDouble() * 2 - 1;
            bl += lowA * (x - bl);
            bh += highA * (x - bh);
            b[i] += 0.5 * (bh - bl) * Smooth(t / 0.9) * (1 - Smooth((t - 2.2) / (length - 2.2)));
        }
        return Finish(b, 0.6);
    }

    private static double[] Boing()
    {
        var b = Buffer(1.1);
        AddVoice(b, 0, 1.1,
            t => (300 + 120 * Math.Exp(-t / 0.25)) * (1 + 0.25 * Math.Sin(TwoPi * 13 * t) * Math.Exp(-t / 0.3)) * (0.5 + 0.5 * Smooth(t / 0.05)),
            t => Math.Exp(-t / 0.45) * Math.Min(1, t / 0.003),
            k => k == 1 ? 1.0 : k == 2 ? 0.35 : k == 3 ? 0.15 : 0.0, 6000);
        return Finish(b, 0.7);
    }

    private static double[] Buzzer()
    {
        var b = Buffer(0.8);
        foreach (double f in new[] { 150.0, 153.0 })
            AddVoice(b, 0, 0.8, t => f, t => Envelope(t, 0.8, 0.005, 0.03),
                k => k % 2 == 1 ? 1.0 / k : 0.0, 8000);
        return Finish(b, 0.6);
    }

    private static double[] Tada()
    {
        var b = Buffer(1.95);
        foreach (double f in new[] { 261.63, 329.63, 392.0, 523.25 })
            AddVoice(b, 0, 0.16, t => f, t => 0.7 * Envelope(t, 0.16, 0.01, 0.04), Brass, 7000);
        foreach (double f in new[] { 392.0, 523.25, 659.25, 783.99 })
            AddVoice(b, 0.2, 1.7, t => f, t => 0.7 * Envelope(t, 1.7, 0.012, 0.45) * (0.6 + 0.4 * Math.Exp(-t / 0.5)), Brass, 7000);
        return Finish(b, 0.7);
    }

    // ---------- building blocks ----------

    private static double Brass(int k) => Math.Exp(-k / 8.0) / Math.Pow(k, 0.5);

    private static double[] Buffer(double seconds) => new double[(int)Math.Round(seconds * Sr)];

    private static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>Rise over <paramref name="attack"/> seconds, fall over the last <paramref name="release"/> seconds.</summary>
    private static double Envelope(double t, double dur, double attack, double release) =>
        Smooth(t / attack) * Smooth((dur - t) / release);

    /// <summary>
    /// Adds a band-limited harmonic tone (partials up to <paramref name="maxHz"/>) that follows a frequency and an
    /// amplitude curve; the times given to the curves start at 0 at the tone's own start.
    /// </summary>
    private static void AddVoice(double[] b, double start, double dur, Func<double, double> freqAt,
        Func<double, double> ampAt, Func<int, double> harmonic, double maxHz)
    {
        int i0 = (int)Math.Round(start * Sr), n = (int)Math.Round(dur * Sr);
        double phase = 0;
        for (int i = 0; i < n && i0 + i < b.Length; i++)
        {
            double t = (double)i / Sr;
            double f = freqAt(t);
            phase += TwoPi * f / Sr;
            if (phase > TwoPi) phase -= TwoPi;
            int kmax = Math.Min(40, (int)(maxHz / f));
            double sum = 0;
            for (int k = 1; k <= kmax; k++)
            {
                double h = harmonic(k);
                if (h != 0) sum += h * Math.Sin(k * phase);
            }
            b[i0 + i] += sum * ampAt(t);
        }
    }

    /// <summary>A snare-like stroke: a short low-passed noise burst plus a fast-decaying thump.</summary>
    private static void Drumstroke(double[] b, double start, double amp, double thumpHz, double tau, Random rng)
    {
        int i0 = (int)Math.Round(start * Sr);
        int n = (int)(tau * 7 * Sr);
        double lp = 0, a = 1 - Math.Exp(-TwoPi * 3000 / Sr);
        for (int i = 0; i < n && i0 + i < b.Length; i++)
        {
            double t = (double)i / Sr;
            lp += a * ((rng.NextDouble() * 2 - 1) - lp);
            double env = Math.Min(1, t / 0.0015) * Math.Exp(-t / tau);
            double thump = 0.6 * Math.Sin(TwoPi * thumpHz * t) * Math.Exp(-t / (tau * 1.6));
            b[i0 + i] += amp * (lp * env + thump);
        }
    }

    /// <summary>A tom: a sine that drops in pitch, plus a little noise for the stick.</summary>
    private static void Tom(double[] b, double start, double fromHz, double toHz, double amp, Random rng)
    {
        int i0 = (int)Math.Round(start * Sr), n = (int)(0.45 * Sr);
        double phase = 0, lp = 0, a = 1 - Math.Exp(-TwoPi * 1500 / Sr);
        for (int i = 0; i < n && i0 + i < b.Length; i++)
        {
            double t = (double)i / Sr;
            double f = PitchDrop(fromHz, toHz, t);
            phase += TwoPi * f / Sr;
            lp += a * ((rng.NextDouble() * 2 - 1) - lp);
            b[i0 + i] += amp * (Math.Sin(phase) * Math.Exp(-t / 0.11) * Math.Min(1, t / 0.001)
                                + 0.35 * lp * Math.Exp(-t / 0.03));
        }

        static double PitchDrop(double from, double to, double t) => to + (from - to) * Math.Exp(-t / 0.04);
    }

    /// <summary>Groups of short high pulses, like a cricket: <paramref name="period"/> seconds between groups.</summary>
    private static void AddChirps(double[] b, double start, double hz, double period, double level)
    {
        const double pulse = 0.018, gap = 0.038;
        for (int g = 0; start + g * period + 4 * gap < b.Length / (double)Sr; g++)
        {
            double groupLevel = level * (0.85 + 0.15 * Math.Sin(g * 1.7));
            for (int p = 0; p < 4; p++)
            {
                int i0 = (int)Math.Round((start + g * period + p * gap) * Sr);
                int n = (int)(pulse * Sr);
                for (int i = 0; i < n && i0 + i < b.Length; i++)
                {
                    double t = (double)i / Sr;
                    double w = Math.Sin(Math.PI * i / n);
                    b[i0 + i] += groupLevel * w * w * Math.Sin(TwoPi * hz * t);
                }
            }
        }
    }

    /// <summary>Removes the mean, fades the first 2 ms and last 25 ms, and sets the peak to <paramref name="peak"/>.</summary>
    private static double[] Finish(double[] b, double peak)
    {
        double mean = 0;
        foreach (double v in b) mean += v;
        mean /= b.Length;
        int fadeIn = (int)(0.002 * Sr), fadeOut = (int)(0.025 * Sr);
        double max = 0;
        for (int i = 0; i < b.Length; i++)
        {
            double v = b[i] - mean;
            if (i < fadeIn) v *= Smooth((double)i / fadeIn);
            int fromEnd = b.Length - 1 - i;
            if (fromEnd < fadeOut) v *= Smooth((double)fromEnd / fadeOut);
            b[i] = v;
            max = Math.Max(max, Math.Abs(v));
        }
        if (max > 0)
        {
            double g = peak / max;
            for (int i = 0; i < b.Length; i++) b[i] *= g;
        }
        return b;
    }

    private static void WriteWav(string path, double[] samples)
    {
        string temp = path + ".tmp";
        try
        {
            using (var w = new WaveFileWriter(temp, new WaveFormat(Sr, 16, 1)))
            {
                var bytes = new byte[samples.Length * 2];
                for (int i = 0; i < samples.Length; i++)
                {
                    short s = (short)Math.Round(Math.Clamp(samples[i], -1, 1) * 32767);
                    bytes[2 * i] = (byte)(s & 0xFF);
                    bytes[2 * i + 1] = (byte)((s >> 8) & 0xFF);
                }
                w.Write(bytes, 0, bytes.Length);
            }
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }
}
