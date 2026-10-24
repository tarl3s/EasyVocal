using System;

namespace EasyVocal.Core.Dsp;

// The five "basic" voice effects. Every one follows the same contract:
//  - Amount is 0..1; Amount 0 returns at once and leaves the buffer bit-exact.
//  - Every buffer is allocated in the constructor; Process never allocates, locks or uses LINQ.
//  - Silence in gives silence out (nothing here makes noise on its own).

internal static class DspUtil
{
    public static double Clamp01(double v) => double.IsNaN(v) ? 0 : v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>Snaps tiny values to zero so decaying tails never fall into slow denormal arithmetic.</summary>
    public static float Flush(float v) => (v < 1e-20f && v > -1e-20f) ? 0f : v;

    public static int Samples(int sampleRate, double seconds) => Math.Max(1, (int)Math.Round(seconds * sampleRate));
}

/// <summary>Cheap xorshift noise: no allocation, deterministic per seed.</summary>
internal struct NoiseSource
{
    private uint state;

    public NoiseSource(uint seed) { state = seed == 0 ? 0x9E3779B9u : seed; }

    public uint NextUInt()
    {
        uint x = state;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        state = x;
        return x;
    }

    /// <summary>Uniform in [-1, 1).</summary>
    public float NextBipolar() => NextUInt() * (2f / 4294967296f) - 1f;
}

/// <summary>Feedback comb filter (the building block of the short room reverb).</summary>
internal sealed class CombFilter
{
    private readonly float[] buf;
    private int pos;

    public CombFilter(int length) { buf = new float[length]; }

    public float Process(float x, float feedback)
    {
        float y = buf[pos];
        buf[pos] = DspUtil.Flush(x + feedback * y);
        if (++pos == buf.Length) pos = 0;
        return y;
    }

    public void Reset() { Array.Clear(buf); pos = 0; }
}

/// <summary>Schroeder all-pass: smears the comb echoes so the reverb sounds like a room, not a metal tube.</summary>
internal sealed class AllPassFilter
{
    private readonly float[] buf;
    private int pos;

    public AllPassFilter(int length) { buf = new float[length]; }

    public float Process(float x, float g)
    {
        float v = buf[pos];
        float w = DspUtil.Flush(x + g * v);
        buf[pos] = w;
        if (++pos == buf.Length) pos = 0;
        return v - g * w;
    }

    public void Reset() { Array.Clear(buf); pos = 0; }
}

/// <summary>Feedback delay (~240 ms) plus a short room reverb. Amount raises the wet mix and the feedback (at most 0.6).</summary>
public sealed class EchoEffect : IAudioEffect
{
    private const float MaxFeedback = 0.6f;
    private readonly float[] delay;
    private int pos;
    private float damp;
    private readonly CombFilter[] combs;
    private readonly AllPassFilter[] allPasses;
    private double amount = 0.5;

    public EchoEffect(int sampleRate)
    {
        delay = new float[DspUtil.Samples(sampleRate, 0.24)];
        combs = new[]
        {
            new CombFilter(DspUtil.Samples(sampleRate, 0.0297)),
            new CombFilter(DspUtil.Samples(sampleRate, 0.0371)),
            new CombFilter(DspUtil.Samples(sampleRate, 0.0411)),
            new CombFilter(DspUtil.Samples(sampleRate, 0.0437)),
        };
        allPasses = new[]
        {
            new AllPassFilter(DspUtil.Samples(sampleRate, 0.005)),
            new AllPassFilter(DspUtil.Samples(sampleRate, 0.0017)),
        };
    }

    public double Amount { get => amount; set => amount = DspUtil.Clamp01(value); }

    public void Process(Span<float> mono)
    {
        double a = amount;
        if (a <= 0) return;

        float fb = (float)(MaxFeedback * a);
        float wet = (float)(0.55 * a) * (1f - fb); // normalised so a held note can't pile up past the dry level
        float dry = (float)(1 - 0.25 * a);
        float room = (float)(0.04 * a);

        for (int i = 0; i < mono.Length; i++)
        {
            float x = mono[i];

            float d = delay[pos];
            damp += 0.5f * (d - damp); // each repeat a little darker than the last
            delay[pos] = DspUtil.Flush(x + fb * damp);
            if (++pos == delay.Length) pos = 0;

            float r = 0;
            for (int c = 0; c < combs.Length; c++) r += combs[c].Process(x, 0.7f);
            r *= 0.25f;
            for (int p = 0; p < allPasses.Length; p++) r = allPasses[p].Process(r, 0.5f);

            mono[i] = x * dry + wet * d + room * r;
        }
    }

    public void Reset()
    {
        Array.Clear(delay);
        pos = 0;
        damp = 0;
        for (int c = 0; c < combs.Length; c++) combs[c].Reset();
        for (int p = 0; p < allPasses.Length; p++) allPasses[p].Reset();
    }
}

/// <summary>Old radio: band-pass 300-3400 Hz, mild saturation and faint crackle that only exists while the voice does.</summary>
public sealed class RadioEffect : IAudioEffect
{
    private readonly Biquad hp1, hp2, lp1, lp2;
    private readonly float releaseCoef;
    private readonly uint crackleThreshold;
    private NoiseSource noise = new NoiseSource(0xC0FFEEu);
    private float env;
    private float click;
    private double amount = 0.5;

    public RadioEffect(int sampleRate)
    {
        // Two cascaded Butterworth sections each side: a steep, telephone-like band.
        hp1 = Biquad.HighPass(sampleRate, 300, 0.5412);
        hp2 = Biquad.HighPass(sampleRate, 300, 1.3066);
        lp1 = Biquad.LowPass(sampleRate, 3400, 0.5412);
        lp2 = Biquad.LowPass(sampleRate, 3400, 1.3066);
        releaseCoef = (float)Math.Exp(-1.0 / (0.05 * sampleRate));
        crackleThreshold = (uint)(uint.MaxValue / (sampleRate / 30.0)); // about 30 crackles a second
    }

    public double Amount { get => amount; set => amount = DspUtil.Clamp01(value); }

    public void Process(Span<float> mono)
    {
        double a = amount;
        if (a <= 0) return;
        float wetMix = (float)a, dryMix = 1f - wetMix;
        float drive = (float)(1 + 0.8 * a);

        for (int i = 0; i < mono.Length; i++)
        {
            float x = mono[i];

            float ax = x < 0 ? -x : x;
            env = ax > env ? ax : DspUtil.Flush(env * releaseCoef);
            float gate = env * 10f;
            if (gate > 1f) gate = 1f;

            float y = lp2.Process(lp1.Process(hp2.Process(hp1.Process(x))));
            y = 1.25f * (float)Math.Tanh(drive * y) / drive;

            if (gate > 0.01f && noise.NextUInt() < crackleThreshold)
                click = 0.12f * noise.NextBipolar();
            click = DspUtil.Flush(click * 0.8f);
            y += click * gate;

            mono[i] = x * dryMix + y * wetMix;
        }
    }

    public void Reset()
    {
        hp1.Reset(); hp2.Reset(); lp1.Reset(); lp2.Reset();
        env = 0;
        click = 0;
    }
}

/// <summary>Distortion: tanh soft clip, the drive grows with Amount and the output level is kept near the input level.</summary>
public sealed class DistortionEffect : IAudioEffect
{
    private double amount = 0.5;

    public DistortionEffect(int sampleRate) { }

    public double Amount { get => amount; set => amount = DspUtil.Clamp01(value); }

    public void Process(Span<float> mono)
    {
        double a = amount;
        if (a <= 0) return;
        double drive = 1 + 9 * a;
        float wetMix = (float)Math.Min(1.0, a * 4), dryMix = 1f - wetMix; // fade in over the first quarter
        float k = (float)drive;
        float comp = (float)(0.5 / Math.Tanh(drive * 0.5)); // a half-scale input comes out half-scale

        for (int i = 0; i < mono.Length; i++)
        {
            float x = mono[i];
            float y = comp * (float)Math.Tanh(k * x);
            mono[i] = x * dryMix + y * wetMix;
        }
    }

    public void Reset() { }
}

/// <summary>Robot: ring modulation (carrier 30-80 Hz, higher with Amount) through a short comb for a metallic tone.</summary>
public sealed class RobotEffect : IAudioEffect
{
    private readonly int sampleRate;
    private readonly float[] comb;
    private int pos;
    private double phase;
    private double amount = 0.5;

    public RobotEffect(int sampleRate)
    {
        this.sampleRate = sampleRate;
        comb = new float[DspUtil.Samples(sampleRate, 0.003)];
    }

    public double Amount { get => amount; set => amount = DspUtil.Clamp01(value); }

    public void Process(Span<float> mono)
    {
        double a = amount;
        if (a <= 0) return;
        double step = 2 * Math.PI * (30 + 50 * a) / sampleRate;
        float wetMix = (float)a, dryMix = 1f - wetMix;

        for (int i = 0; i < mono.Length; i++)
        {
            float x = mono[i];

            float ring = x * (float)Math.Sin(phase);
            phase += step;
            if (phase > 2 * Math.PI) phase -= 2 * Math.PI;

            float old = comb[pos];
            comb[pos] = ring;
            if (++pos == comb.Length) pos = 0;
            float y = (ring + 0.5f * old) * (1.25f / 1.5f); // ring mod loses level; bring it back

            mono[i] = x * dryMix + y * wetMix;
        }
    }

    public void Reset()
    {
        Array.Clear(comb);
        pos = 0;
        phase = 0;
    }
}

/// <summary>
/// Whisper: the voice is replaced by noise shaped by the voice's own spectral envelope
/// (a band-pass filterbank; each band's noise follows that band's energy). Fades out when the voice stops.
/// </summary>
public sealed class WhisperEffect : IAudioEffect
{
    private const int Bands = 9;
    private readonly Biquad[] analysis = new Biquad[Bands];
    private readonly Biquad[] synthesis = new Biquad[Bands];
    private readonly float[] power = new float[Bands];
    private readonly float[] bandGain = new float[Bands]; // makes unit-variance noise through each band have unit power
    private readonly float smooth;
    private NoiseSource noise = new NoiseSource(0xBADC0DEu);
    private double amount = 0.5;

    public WhisperEffect(int sampleRate)
    {
        double lo = 300, hi = Math.Min(6500, sampleRate * 0.45);
        var calib = new NoiseSource(12345u);
        int calibLen = sampleRate / 2;
        for (int b = 0; b < Bands; b++)
        {
            double fc = lo * Math.Pow(hi / lo, b / (double)(Bands - 1));
            analysis[b] = Biquad.BandPass(sampleRate, fc, 1.8);
            synthesis[b] = Biquad.BandPass(sampleRate, fc, 1.8);

            var probe = Biquad.BandPass(sampleRate, fc, 1.8);
            double sum = 0;
            for (int n = 0; n < calibLen; n++)
            {
                float y = probe.Process(calib.NextBipolar() * 1.7320508f);
                sum += (double)y * y;
            }
            bandGain[b] = (float)(1.0 / Math.Sqrt(Math.Max(sum / calibLen, 1e-12)));
        }
        smooth = (float)(1 - Math.Exp(-1.0 / (0.015 * sampleRate)));
    }

    public double Amount { get => amount; set => amount = DspUtil.Clamp01(value); }

    public void Process(Span<float> mono)
    {
        double a = amount;
        if (a <= 0) return;
        float wetMix = (float)a, dryMix = 1f - wetMix;

        for (int i = 0; i < mono.Length; i++)
        {
            float x = mono[i];
            float n = noise.NextBipolar() * 1.7320508f; // unit variance
            float total = 0, sum = 0;
            for (int b = 0; b < Bands; b++)
            {
                float band = analysis[b].Process(x);
                power[b] = DspUtil.Flush(power[b] + smooth * (band * band - power[b]));
                total += power[b];
                sum += synthesis[b].Process(n) * bandGain[b] * (float)Math.Sqrt(power[b]);
            }

            float gate = (float)Math.Sqrt(total) * (1f / 0.003f); // below about -50 dB the whisper fades out
            if (gate > 1f) gate = 1f;
            float y = (float)Math.Tanh(0.9f * sum * gate);

            mono[i] = x * dryMix + y * wetMix;
        }
    }

    public void Reset()
    {
        for (int b = 0; b < Bands; b++)
        {
            analysis[b].Reset();
            synthesis[b].Reset();
            power[b] = 0;
        }
    }
}
