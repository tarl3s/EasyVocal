using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EasyVocal.Core.Dsp;

namespace EasyVocal.Tests.Helpers;

/// <summary>
/// What a voice sounds like, in numbers, measured on a voice's output for a known speech input (we can't listen):
/// pitch and how much it moves, how much is voiced, brightness and tilt of the spectrum, how noisy it is, how rough
/// (fast level flutter), how long the room rings after the last word, and the energy an octave below the pitch.
/// <see cref="Distance"/> combines the first ones, each in "clearly audible step" units.
/// </summary>
public sealed record VoiceFeatures
{
    private const int Sr = AudioFormat.SampleRate;
    private const int Frame = 2048, Hop = 1024;
    private const int Dec = 4;                       // pitch is tracked at 12 kHz
    private const double DecRate = (double)Sr / Dec;

    /// <summary>Median pitch of the voiced frames (Hz); NaN when almost nothing is voiced.</summary>
    public double PitchHz { get; private init; }
    /// <summary>How much the pitch moves: the spread (interquartile range) of the voiced frames' pitch, in octaves.</summary>
    public double PitchSpread { get; private init; }
    /// <summary>Share of the speech frames that have a clear pitch.</summary>
    public double VoicedShare { get; private init; }
    /// <summary>Spectral centroid of the speech (Hz, 80–8000 Hz).</summary>
    public double Centroid { get; private init; }
    /// <summary>Energy below 500 Hz against 2–8 kHz, in dB (higher = darker / more chest).</summary>
    public double TiltDb { get; private init; }
    /// <summary>Spectral flatness 100–6000 Hz averaged over speech frames (0 = pure tones, 1 = white noise).</summary>
    public double Flatness { get; private init; }
    /// <summary>How deep the level flutters 25–65 times a second while speaking (modulation depth, 0 = steady; higher = rougher).</summary>
    public double Roughness { get; private init; }
    /// <summary>Energy 0.15–0.65 s after the last word against the speech, in dB (-90..0; higher = bigger room).</summary>
    public double TailDb { get; private init; }
    /// <summary>Energy an octave below the expected pitch against the expected pitch itself, in dB (a layer an octave down raises it).</summary>
    public double SubOctaveDb { get; private init; }
    /// <summary>How far the voice is from scaling with its input (0 = linear, no distortion); set by the caller.</summary>
    public double Nonlinearity { get; init; }
    /// <summary>Loudness of the speech frames against the input's, in dB.</summary>
    public double LevelDb { get; private init; }

    /// <param name="output">The voice's output for <paramref name="input"/> (same length, delayed by <paramref name="latency"/>).</param>
    /// <param name="expectedRatio">The voice's pitch ratio (for <see cref="SubOctaveDb"/>).</param>
    public static VoiceFeatures Measure(float[] output, float[] input, int latency, double expectedRatio)
    {
        var inFrames = Frames(input, 0);
        double loudest = inFrames.Max(f => f.Rms);
        var speech = inFrames.Where(f => f.Rms > loudest * 0.06).Select(f => f.Start).ToList(); // within 25 dB of the loudest
        int lastSpeech = LastActive(input, 0.01f);

        var pitches = new List<double>();
        double specLow = 0, specHigh = 0, num = 0, den = 0, flat = 0, sub = 0, fund = 0, outPower = 0, inPower = 0;
        int voiced = 0;
        var re = new double[Frame];
        var im = new double[Frame];
        foreach (int start in speech)
        {
            int at = start + latency;
            if (at + Frame > output.Length) continue;
            double hz = Yin(output, at);
            if (hz > 0) { pitches.Add(Math.Log2(hz)); voiced++; }

            for (int i = 0; i < Frame; i++) { outPower += (double)output[at + i] * output[at + i]; inPower += (double)input[start + i] * input[start + i]; }
            Spectrum(output, at, re, im);
            double logSum = 0, linSum = 0;
            int bins = 0;
            for (int k = 1; k < Frame / 2; k++)
            {
                double f = (double)k * Sr / Frame, p = re[k] * re[k] + im[k] * im[k];
                if (f >= 80 && f <= 8000) { num += f * p; den += p; }
                if (f >= 80 && f < 500) specLow += p;
                if (f >= 2000 && f <= 8000) specHigh += p;
                if (f >= 100 && f <= 6000) { logSum += Math.Log(p + 1e-18); linSum += p; bins++; }
            }
            if (bins > 0 && linSum > 1e-15) flat += Math.Exp(logSum / bins) / (linSum / bins);

            double inHz = Yin(input, start);
            if (inHz > 0)
            {
                double f0 = inHz * expectedRatio;
                fund += Goertzel(output, at, f0);
                sub += Goertzel(output, at, f0 / 2);
            }
        }

        pitches.Sort();
        double median = pitches.Count > 0 ? pitches[pitches.Count / 2] : double.NaN;
        double spread = pitches.Count >= 4 ? pitches[pitches.Count * 3 / 4] - pitches[pitches.Count / 4] : 0;
        int n = Math.Max(1, speech.Count);
        return new VoiceFeatures
        {
            PitchHz = (double)voiced / n < 0.15 ? double.NaN : Math.Pow(2, median),
            PitchSpread = spread,
            VoicedShare = (double)voiced / n,
            Centroid = den > 0 ? num / den : 0,
            TiltDb = 10 * Math.Log10((specLow + 1e-15) / (specHigh + 1e-15)),
            Flatness = flat / n,
            Roughness = Flutter(output, speech, latency),
            TailDb = Tail(output, input, lastSpeech + latency),
            SubOctaveDb = 10 * Math.Log10((sub + 1e-15) / (fund + 1e-15)),
            LevelDb = 10 * Math.Log10((outPower + 1e-15) / (inPower + 1e-15)),
        };
    }

    /// <summary>
    /// Distance between two voices, each feature divided by a step that is clearly audible (3 semitones of pitch,
    /// a quarter octave of brightness, 6 dB of tilt, 10 dB of room…). Above 1 = they sound different.
    /// </summary>
    public static double Distance(VoiceFeatures a, VoiceFeatures b, double inputPitchHz)
    {
        double pa = double.IsNaN(a.PitchHz) ? inputPitchHz : a.PitchHz, pb = double.IsNaN(b.PitchHz) ? inputPitchHz : b.PitchHz;
        double sum = 0;
        void Add(double x, double y, double step) { double d = (x - y) / step; sum += d * d; }
        Add(Math.Log2(pa), Math.Log2(pb), 0.25);
        Add(a.PitchSpread, b.PitchSpread, 0.12);
        Add(a.VoicedShare, b.VoicedShare, 0.3);
        Add(Math.Log2(Math.Max(a.Centroid, 1)), Math.Log2(Math.Max(b.Centroid, 1)), 0.25);
        Add(a.TiltDb, b.TiltDb, 6);
        Add(a.Flatness, b.Flatness, 0.1);
        Add(a.Roughness, b.Roughness, 0.12);
        Add(Math.Max(a.TailDb, -70), Math.Max(b.TailDb, -70), 10);
        Add(a.Nonlinearity, b.Nonlinearity, 0.15);
        return Math.Sqrt(sum);
    }

    public override string ToString() => string.Format(CultureInfo.InvariantCulture,
        "pitch {0,5:0} Hz  spread {1:0.00} oct  voiced {2:P0}  centroid {3,5:0} Hz  tilt {4,5:0.0} dB  flat {5:0.000}  rough {6,5:0.00}  tail {7,5:0} dB  sub {8,5:0.0} dB  nonlin {9:0.000}  level {10,5:0.0} dB",
        PitchHz, PitchSpread, VoicedShare, Centroid, TiltDb, Flatness, Roughness, TailDb, SubOctaveDb, Nonlinearity, LevelDb);

    // ---------------------------------------------------------------- pieces

    private readonly record struct FrameInfo(int Start, double Rms);

    private static List<FrameInfo> Frames(float[] x, int offset)
    {
        var list = new List<FrameInfo>();
        for (int s = offset; s + Frame <= x.Length; s += Hop)
        {
            double e = 0;
            for (int i = 0; i < Frame; i++) e += (double)x[s + i] * x[s + i];
            list.Add(new FrameInfo(s, Math.Sqrt(e / Frame)));
        }
        return list;
    }

    private static int LastActive(float[] x, float threshold)
    {
        for (int i = x.Length - 1; i >= 0; i--)
            if (Math.Abs(x[i]) > threshold) return i;
        return 0;
    }

    /// <summary>YIN pitch of the frame at <paramref name="start"/> (60–500 Hz, on a 12 kHz low-passed copy); 0 = unvoiced.</summary>
    private static double Yin(float[] x, int start)
    {
        int len = Frame / Dec;
        var d = new double[len];
        var lp1 = Biquad.LowPass(Sr, 2200);
        var lp2 = Biquad.LowPass(Sr, 2200);
        int warm = Math.Min(start, 256);
        for (int i = start - warm; i < start; i++) lp2.Process(lp1.Process(x[i]));
        int j = 0;
        double energy = 0;
        for (int i = start; i < start + Frame && j < len; i++)
        {
            float v = lp2.Process(lp1.Process(x[i]));
            if ((i - start) % Dec == 0) { d[j++] = v; energy += v * v; }
        }
        if (energy / len < 1e-7) return 0;
        int minLag = (int)(DecRate / 500), maxLag = (int)(DecRate / 60), win = len - maxLag - 1;
        var cmnd = new double[maxLag + 2];
        double cum = 0;
        cmnd[0] = 1;
        for (int tau = 1; tau <= maxLag + 1; tau++)
        {
            double s = 0;
            for (int i = 0; i < win; i++) { double e = d[i] - d[i + tau]; s += e * e; }
            cum += s;
            cmnd[tau] = cum > 1e-20 ? s * tau / cum : 1;
        }
        for (int tau = minLag; tau <= maxLag; tau++)
        {
            if (cmnd[tau] < 0.2)
            {
                while (tau + 1 <= maxLag && cmnd[tau + 1] < cmnd[tau]) tau++;
                double a = cmnd[tau - 1], b = cmnd[tau], c = cmnd[tau + 1], den = a - 2 * b + c;
                double lag = tau + (den > 1e-12 ? Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5) : 0);
                return DecRate / lag;
            }
        }
        return 0;
    }

    private static void Spectrum(float[] x, int start, double[] re, double[] im)
    {
        for (int i = 0; i < Frame; i++)
        {
            re[i] = x[start + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Frame - 1)));
            im[i] = 0;
        }
        Signals.Fft(re, im);
    }

    /// <summary>Power at one frequency in the frame (Hann-windowed Goertzel).</summary>
    private static double Goertzel(float[] x, int start, double hz)
    {
        double w = 2 * Math.PI * hz / Sr, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int i = 0; i < Frame; i++)
        {
            double v = x[start + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Frame - 1)));
            double s = v + c * s1 - s2;
            s2 = s1; s1 = s;
        }
        return s1 * s1 + s2 * s2 - c * s1 * s2;
    }

    /// <summary>
    /// The level (rectified, then a steep 70 Hz low-pass so the pitch itself stays out, one value per ms) over
    /// 128 ms around each speech frame: the depth of its 25–65 Hz movement against its mean, averaged.
    /// </summary>
    private static double Flutter(float[] y, List<int> speechStarts, int latency)
    {
        var lp = new[] { Biquad.LowPass(Sr, 70, 0.5098), Biquad.LowPass(Sr, 70, 0.6013), Biquad.LowPass(Sr, 70, 0.9000), Biquad.LowPass(Sr, 70, 2.5629) };
        var env = new double[y.Length / 48 + 1];
        for (int i = 0; i < y.Length; i++)
        {
            float v = Math.Abs(y[i]);
            foreach (var f in lp) v = f.Process(v);
            if (i % 48 == 0) env[i / 48] = v;
        }
        const int W = 128;
        double total = 0;
        int frames = 0;
        foreach (int s in speechStarts)
        {
            int a = (s + latency) / 48;
            if (a + W > env.Length) continue;
            double mean = 0, wsum = 0;
            for (int i = 0; i < W; i++) mean += env[a + i];
            mean /= W;
            if (mean < 1e-4) continue;
            double power = 0;
            for (int k = 4; k <= 8; k++) // 31–62 Hz at 1 kHz over 128 values
            {
                double re = 0, im = 0;
                for (int i = 0; i < W; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (W - 1));
                    double v = (env[a + i] - mean) * w;
                    re += v * Math.Cos(2 * Math.PI * k * i / W);
                    im -= v * Math.Sin(2 * Math.PI * k * i / W);
                    if (k == 4) wsum += w;
                }
                power += re * re + im * im;
            }
            total += 2 * Math.Sqrt(power) / wsum / mean;
            frames++;
        }
        return frames == 0 ? 0 : total / frames;
    }

    private static double Tail(float[] y, float[] input, int end)
    {
        double speech = 0;
        int count = 0;
        for (int i = 0; i < input.Length; i++)
            if (Math.Abs(input[i]) > 0.01f) { speech += (double)input[i] * input[i]; count++; }
        speech /= Math.Max(1, count);
        int a = end + (int)(0.15 * Sr), b = Math.Min(y.Length, end + (int)(0.65 * Sr));
        if (b <= a) return -90;
        double tail = 0;
        for (int i = a; i < b; i++) tail += (double)y[i] * y[i];
        tail /= b - a;
        return Math.Max(-90, 10 * Math.Log10((tail + 1e-20) / (speech + 1e-20)));
    }
}
