using System;

namespace EasyVocal.Core.Dsp;

/// <summary>
/// Second-order IIR filter (RBJ audio-EQ-cookbook coefficients, transposed direct form II).
/// Build one with a static factory, or re-tune an existing one with the Set* methods (no allocation).
/// </summary>
public sealed class Biquad
{
    private double b0 = 1, b1, b2, a1, a2;
    private double z1, z2;

    public static Biquad LowPass(int sampleRate, double freq, double q = 0.7071)
    {
        var f = new Biquad();
        f.SetLowPass(sampleRate, freq, q);
        return f;
    }

    public static Biquad HighPass(int sampleRate, double freq, double q = 0.7071)
    {
        var f = new Biquad();
        f.SetHighPass(sampleRate, freq, q);
        return f;
    }

    /// <summary>Band-pass with 0 dB gain at the centre frequency.</summary>
    public static Biquad BandPass(int sampleRate, double freq, double q)
    {
        var f = new Biquad();
        f.SetBandPass(sampleRate, freq, q);
        return f;
    }

    public static Biquad Peaking(int sampleRate, double freq, double q, double gainDb)
    {
        var f = new Biquad();
        f.SetPeaking(sampleRate, freq, q, gainDb);
        return f;
    }

    public void SetLowPass(int sampleRate, double freq, double q = 0.7071)
    {
        Prepare(sampleRate, freq, q, out double cosW, out double alpha);
        Set((1 - cosW) / 2, 1 - cosW, (1 - cosW) / 2, 1 + alpha, -2 * cosW, 1 - alpha);
    }

    public void SetHighPass(int sampleRate, double freq, double q = 0.7071)
    {
        Prepare(sampleRate, freq, q, out double cosW, out double alpha);
        Set((1 + cosW) / 2, -(1 + cosW), (1 + cosW) / 2, 1 + alpha, -2 * cosW, 1 - alpha);
    }

    public void SetBandPass(int sampleRate, double freq, double q)
    {
        Prepare(sampleRate, freq, q, out double cosW, out double alpha);
        Set(alpha, 0, -alpha, 1 + alpha, -2 * cosW, 1 - alpha);
    }

    public void SetPeaking(int sampleRate, double freq, double q, double gainDb)
    {
        Prepare(sampleRate, freq, q, out double cosW, out double alpha);
        double A = Math.Pow(10, gainDb / 40);
        Set(1 + alpha * A, -2 * cosW, 1 - alpha * A, 1 + alpha / A, -2 * cosW, 1 - alpha / A);
    }

    public float Process(float x)
    {
        double y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        if (z1 < 1e-30 && z1 > -1e-30) z1 = 0; // no denormal slowdown in decaying tails
        if (z2 < 1e-30 && z2 > -1e-30) z2 = 0;
        return (float)y;
    }

    public void Reset() { z1 = 0; z2 = 0; }

    private static void Prepare(int sampleRate, double freq, double q, out double cosW, out double alpha)
    {
        // Keep the corner strictly below Nyquist so the coefficients stay stable.
        freq = Math.Clamp(freq, 1, sampleRate * 0.499);
        double w = 2 * Math.PI * freq / sampleRate;
        cosW = Math.Cos(w);
        alpha = Math.Sin(w) / (2 * Math.Max(q, 0.01));
    }

    private void Set(double nb0, double nb1, double nb2, double na0, double na1, double na2)
    {
        b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0;
        a1 = na1 / na0; a2 = na2 / na0;
    }
}
