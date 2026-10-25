using System;

namespace EasyVocal.Core.Dsp;

// The effects that give the built-in voices their own character (Growl, Big room, Tone, Vocoder). Same contract
// as Effects.cs: Amount 0 (or a neutral tone) leaves the buffer bit-exact, every buffer is allocated in the
// constructor, Process never allocates, locks or uses LINQ, and silence in gives silence out.

/// <summary>
/// Growl: a rough, gravelly voice. The level flutters 30–50 times a second (the rate wanders a little, like a real
/// throat rattle, so it never sounds like a steady tremolo), then a gentle saturation adds rasp.
/// </summary>
public sealed class GrowlEffect : IAmountEffect
{
    private const double BaseHz = 40, SpreadHz = 9;
    private readonly int sampleRate;
    private readonly float jitterCoef, jitterScale;
    private NoiseSource noise = new NoiseSource(0x6A0Fu);
    private float jitter;
    private double phase;
    private double amount = 0.5;

    public GrowlEffect(int sampleRate)
    {
        this.sampleRate = sampleRate;
        double c = 1 - Math.Exp(-2 * Math.PI * 4.0 / sampleRate); // the rate wanders at about 4 Hz
        jitterCoef = (float)c;
        jitterScale = (float)(1 / Math.Sqrt(c / (2 - c))); // unit spread for unit-variance noise in
    }

    public double Amount { get => amount; set => amount = DspUtil.Clamp01(value); }

    public void Process(Span<float> mono)
    {
        double a = amount;
        if (a <= 0) return;
        double depth = 0.85 * a;
        // Keep the level: the mean square of (1 - depth * (0.5 + 0.5 sin)) is (1 - depth/2)² + depth²/8.
        float level = (float)(1 / Math.Sqrt((1 - depth / 2) * (1 - depth / 2) + depth * depth / 8));
        double drive = 1 + 2.5 * a;
        float k = (float)drive, comp = (float)(0.25 / Math.Tanh(0.25 * drive)); // a quarter-scale input keeps its level
        float wetMix = (float)Math.Min(1.0, a * 3), dryMix = 1f - wetMix;
        double step = 2 * Math.PI / sampleRate;

        for (int i = 0; i < mono.Length; i++)
        {
            float x = mono[i];
            jitter += jitterCoef * (noise.NextBipolar() * 1.7320508f - jitter);
            float j = jitter * jitterScale;
            if (j > 1.5f) j = 1.5f; else if (j < -1.5f) j = -1.5f;
            phase += step * (BaseHz + SpreadHz * j);
            if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
            float g = (float)(1 - depth * (0.5 + 0.5 * Math.Sin(phase))) * level;
            float y = comp * (float)Math.Tanh(k * x * g);
            mono[i] = x * dryMix + y * wetMix;
        }
    }

    public void Reset()
    {
        jitter = 0;
        phase = 0;
    }
}

/// <summary>Freeverb-style comb with a damping low-pass in its loop (each round trip a little darker).</summary>
internal sealed class DampedComb
{
    private readonly float[] buf;
    private int pos;
    private float low;

    public DampedComb(int length) { buf = new float[length]; }

    public int Length => buf.Length;

    public float Process(float x, float feedback, float damp)
    {
        float y = buf[pos];
        low = DspUtil.Flush(y + damp * (low - y));
        buf[pos] = DspUtil.Flush(x + feedback * low);
        if (++pos == buf.Length) pos = 0;
        return y;
    }

    public void Reset() { Array.Clear(buf); pos = 0; low = 0; }
}

/// <summary>
/// "Big room": a hall reverb whose size grows with Amount — from a small room (half a second) to a huge hall
/// (about 3 s) with a longer gap before the first reflections (pre-delay), which is what makes a space sound big.
/// Eight damped combs and four all-passes; the wet level is normalised so a bigger room doesn't get louder.
/// </summary>
public sealed class ReverbEffect : IAmountEffect
{
    private static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 }; // Freeverb, 44.1 kHz
    private static readonly int[] AllPassTunings = { 556, 441, 341, 225 };
    private const double MaxPreDelay = 0.090;

    private readonly int sampleRate;
    private readonly DampedComb[] combs;
    private readonly AllPassFilter[] allPasses;
    private readonly float[] feedback;
    private readonly float[] pre;
    private readonly Biquad lowCut;
    private int prePos;
    private double preDelay = -1;           // the pre-delay in use (samples); it glides to a new size instead of jumping
    private double amount = 0.5;

    public ReverbEffect(int sampleRate)
    {
        this.sampleRate = sampleRate;
        double scale = 1.25 * sampleRate / 44100.0;
        combs = new DampedComb[CombTunings.Length];
        for (int c = 0; c < combs.Length; c++) combs[c] = new DampedComb(Math.Max(1, (int)(CombTunings[c] * scale)));
        allPasses = new AllPassFilter[AllPassTunings.Length];
        for (int p = 0; p < allPasses.Length; p++) allPasses[p] = new AllPassFilter(Math.Max(1, (int)(AllPassTunings[p] * scale)));
        feedback = new float[combs.Length];
        pre = new float[DspUtil.Samples(sampleRate, MaxPreDelay) + 4];
        lowCut = Biquad.HighPass(sampleRate, 140); // a room doesn't boom: the lowest notes stay out of the tail
    }

    public double Amount { get => amount; set => amount = DspUtil.Clamp01(value); }

    /// <summary>Seconds for the tail to fall 60 dB at this size.</summary>
    public static double DecaySeconds(double amount) => 0.4 + 2.6 * Math.Pow(DspUtil.Clamp01(amount), 1.5);

    public void Process(Span<float> mono)
    {
        double a = amount;
        if (a <= 0) return;
        double rt60 = DecaySeconds(a);
        double g2 = 0;
        for (int c = 0; c < combs.Length; c++)
        {
            double g = Math.Pow(10, -3.0 * combs[c].Length / (sampleRate * rt60));
            feedback[c] = (float)g;
            g2 += g * g;
        }
        g2 /= combs.Length;
        // White noise through one comb has 1 / (1 - g²) times its power; eight of them add up.
        float wet = (float)(0.9 * Math.Pow(a, 0.6) * Math.Sqrt((1 - g2) / combs.Length));
        float dry = (float)(1 - 0.35 * a);
        float damp = (float)(0.2 + 0.2 * a);
        double preTarget = (0.008 + 0.08 * a * a) * sampleRate;
        if (preDelay < 0) preDelay = preTarget;
        int len = pre.Length;

        for (int i = 0; i < mono.Length; i++)
        {
            float x = mono[i];
            pre[prePos] = lowCut.Process(x);
            if (preDelay != preTarget)
                preDelay = Math.Abs(preTarget - preDelay) <= 0.25 ? preTarget : preDelay + (preTarget > preDelay ? 0.25 : -0.25);
            double rp = prePos - preDelay;
            if (rp < 0) rp += len;
            int r0 = (int)rp;
            float f = (float)(rp - r0);
            int r1 = r0 + 1 == len ? 0 : r0 + 1;
            float d = pre[r0] + (pre[r1] - pre[r0]) * f;
            if (++prePos == len) prePos = 0;

            float r = 0;
            for (int c = 0; c < combs.Length; c++) r += combs[c].Process(d, feedback[c], damp);
            for (int p = 0; p < allPasses.Length; p++) r = allPasses[p].Process(r, 0.5f);
            mono[i] = x * dry + wet * r;
        }
    }

    public void Reset()
    {
        for (int c = 0; c < combs.Length; c++) combs[c].Reset();
        for (int p = 0; p < allPasses.Length; p++) allPasses[p].Reset();
        Array.Clear(pre);
        prePos = 0;
        preDelay = -1;
        lowCut.Reset();
    }
}

/// <summary>
/// Bass and treble (-1..1 each, ±9 dB): a low shelf at 250 Hz (chest) and a high shelf at 2.5 kHz (air). Both 0 =
/// untouched. The filters are re-tuned only when a value changed.
/// </summary>
public sealed class ToneEffect : IAudioEffect
{
    public const double MaxDb = 9;
    private readonly int sampleRate;
    private readonly Biquad low = new(), high = new();
    private double bass, treble;
    private double tunedBass = double.NaN, tunedTreble = double.NaN;

    public ToneEffect(int sampleRate) { this.sampleRate = sampleRate; }

    public double Bass { get => bass; set => bass = Clamp(value); }
    public double Treble { get => treble; set => treble = Clamp(value); }

    /// <summary>Either knob is off the middle.</summary>
    public bool Active => bass != 0 || treble != 0;

    private static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, -1, 1);

    public void Process(Span<float> mono)
    {
        double b = bass, t = treble;
        if (b == 0 && t == 0) return;
        if (b != tunedBass) { low.SetLowShelf(sampleRate, 250, b * MaxDb); tunedBass = b; }
        if (t != tunedTreble) { high.SetHighShelf(sampleRate, 2500, t * MaxDb); tunedTreble = t; }
        for (int i = 0; i < mono.Length; i++) mono[i] = high.Process(low.Process(mono[i]));
    }

    public void Reset() { low.Reset(); high.Reset(); }
}

/// <summary>
/// Vocoder (the classic "movie robot"): the words are kept, the voice itself is replaced by a buzzing synthesizer
/// note at one fixed pitch. Sixteen bands follow the voice's loudness; the same bands of the buzz (plus a little
/// hiss for "s" and "f") are played at those loudnesses. Fades out when the voice stops.
/// </summary>
public sealed class VocoderEffect : IAmountEffect
{
    public const double CarrierHz = 98;
    private const int Bands = 16;
    private const double Q = 4.5;
    private const float Hiss = 0.3f;
    private readonly Biquad[] analysis = new Biquad[Bands];
    private readonly Biquad[] synthesis = new Biquad[Bands];
    private readonly float[] power = new float[Bands];
    private readonly float[] bandGain = new float[Bands]; // makes the carrier through each band unit power
    private readonly float smooth;
    private readonly double dt;
    private double phase;
    private NoiseSource noise = new NoiseSource(0x5EEDu);
    private double amount = 0.5;

    public VocoderEffect(int sampleRate)
    {
        dt = CarrierHz / sampleRate;
        double lo = 150, hi = Math.Min(7000, sampleRate * 0.45);
        int calibLen = sampleRate / 2;
        for (int b = 0; b < Bands; b++)
        {
            double fc = lo * Math.Pow(hi / lo, b / (double)(Bands - 1));
            analysis[b] = Biquad.BandPass(sampleRate, fc, Q);
            synthesis[b] = Biquad.BandPass(sampleRate, fc, Q);

            var probe = Biquad.BandPass(sampleRate, fc, Q);
            double p = 0, sum = 0;
            var calib = new NoiseSource(0x5EEDu);
            for (int n = 0; n < calibLen; n++)
            {
                float y = probe.Process(Carrier(ref p, ref calib, dt));
                if (n >= calibLen / 4) sum += (double)y * y; // after the band settled
            }
            bandGain[b] = (float)(1.0 / Math.Sqrt(Math.Max(sum / (calibLen - calibLen / 4), 1e-12)));
        }
        smooth = (float)(1 - Math.Exp(-1.0 / (0.008 * sampleRate)));
    }

    public double Amount { get => amount; set => amount = DspUtil.Clamp01(value); }

    /// <summary>A band-limited sawtooth (PolyBLEP) plus a little hiss.</summary>
    private static float Carrier(ref double phase, ref NoiseSource noise, double dt)
    {
        double t = phase;
        double saw = 2 * t - 1;
        if (t < dt) { double u = t / dt; saw -= u + u - u * u - 1; }
        else if (t > 1 - dt) { double u = (t - 1) / dt; saw -= u * u + u + u + 1; }
        phase += dt;
        if (phase >= 1) phase -= 1;
        return (float)saw + Hiss * noise.NextBipolar();
    }

    public void Process(Span<float> mono)
    {
        double a = amount;
        if (a <= 0) return;
        float wetMix = (float)a, dryMix = 1f - wetMix;

        for (int i = 0; i < mono.Length; i++)
        {
            float x = mono[i];
            float c = Carrier(ref phase, ref noise, dt);
            float total = 0, sum = 0;
            for (int b = 0; b < Bands; b++)
            {
                float band = analysis[b].Process(x);
                power[b] = DspUtil.Flush(power[b] + smooth * (band * band - power[b]));
                total += power[b];
                sum += synthesis[b].Process(c) * bandGain[b] * (float)Math.Sqrt(power[b]);
            }
            float gate = (float)Math.Sqrt(total) * (1f / 0.003f); // below about -50 dB the buzz fades out
            if (gate > 1f) gate = 1f;
            float y = (float)Math.Tanh(sum * gate);
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
        phase = 0;
    }
}
