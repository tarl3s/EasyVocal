using EasyVocal.Ai.Dsp;
using EasyVocal.Ai.Runtime;
using Microsoft.ML.OnnxRuntime;

namespace EasyVocal.Ai.Rvc;

/// <summary>A pitch tracker for 16 kHz audio: one value per 10 ms frame (1 + length/160 frames), 0 = unvoiced.</summary>
public interface IPitchEstimator : IDisposable
{
    string Name { get; }

    /// <summary>The window it wants to see for the newest <paramref name="samples"/> (RMVPE rounds up to its chunks).</summary>
    int WindowFor(int samples);

    void Estimate(ReadOnlySpan<float> x16, Span<float> f0);
}

/// <summary>
/// RMVPE (lj1995/VoiceConversionWebUI rmvpe.onnx, MIT; RMVPE Apache-2.0): log-mel → 360 pitch bins → cents, decoded
/// as RVC does (argmax ± 4 bins weighted, threshold 0.03). The Balanced and Max levels' tracker. One instance per
/// stream (its buffers); the model is shared.
/// </summary>
public sealed class Rmvpe : IPitchEstimator
{
    private static readonly float[] Cents = MakeCents();
    private readonly OnnxModel model;
    private readonly Mel mel = new();
    private readonly string[] inNames = { "input" }, outNames;
    private int cachedPad = -1;
    private float[] melBuf = Array.Empty<float>(), outBuf = Array.Empty<float>();
    private OrtValue? inVal, outVal;

    public float Threshold { get; set; } = 0.03f;

    public Rmvpe(OnnxModel model)
    {
        this.model = model;
        outNames = new[] { model.OutputNames[0] };
    }

    public string Name => "rmvpe";

    public int WindowFor(int samples) => 5120 * ((samples - 1) / 5120 + 1) - 160;

    private static float[] MakeCents()
    {
        var c = new float[368];
        for (int i = 4; i < 364; i++) c[i] = (float)(20 * (i - 4) + 1997.3794084376191);
        return c;
    }

    public void Estimate(ReadOnlySpan<float> x, Span<float> f0)
    {
        int n = Mel.Frames(x.Length);
        int np = 32 * ((n - 1) / 32 + 1);
        if (np != cachedPad)
        {
            inVal?.Dispose();
            outVal?.Dispose();
            melBuf = new float[Mel.NMels * np];
            outBuf = new float[np * 360];
            inVal = OrtValue.CreateTensorValueFromMemory(melBuf, new long[] { 1, Mel.NMels, np });
            outVal = OrtValue.CreateTensorValueFromMemory(outBuf, new long[] { 1, np, 360 });
            cachedPad = np;
        }
        Array.Clear(melBuf);
        mel.Compute(x, melBuf, np);
        model.Run(inNames, new[] { inVal! }, outNames, new[] { outVal! });
        int frames = Math.Min(n, f0.Length);
        for (int i = 0; i < frames; i++)
        {
            var row = outBuf.AsSpan(i * 360, 360);
            int c = 0;
            float mx = row[0];
            for (int k = 1; k < 360; k++) if (row[k] > mx) { mx = row[k]; c = k; }
            if (mx <= Threshold) { f0[i] = 0; continue; }
            c += 4;
            double ps = 0, ws = 0;
            for (int k = c - 4; k <= c + 4; k++)
            {
                int j = k - 4;
                float s = (j >= 0 && j < 360) ? row[j] : 0;
                ps += s * Cents[k];
                ws += s;
            }
            f0[i] = ws > 0 ? (float)(10 * Math.Pow(2, ps / ws / 1200)) : 0;
        }
        for (int i = frames; i < f0.Length; i++) f0[i] = 0;
    }

    public void Dispose()
    {
        inVal?.Dispose();
        outVal?.Dispose();
        inVal = outVal = null;
    }
}

/// <summary>YIN on 16 kHz audio (no model, ~2 ms per block): the Light level's tracker. 60-900 Hz.</summary>
public sealed class Yin : IPitchEstimator
{
    private const int W = 480, MaxTau = 16000 / 60, MinTau = 16000 / 900;
    private readonly float[] d = new float[MaxTau + 2];

    public string Name => "yin";

    public float Threshold { get; set; } = 0.15f;

    /// <summary>Deepest dip still taken as voiced when none is under <see cref="Threshold"/>.</summary>
    public float Fallback { get; set; } = 0.35f;

    public int WindowFor(int samples) => samples;

    public void Estimate(ReadOnlySpan<float> x, Span<float> f0)
    {
        int n = Math.Min(f0.Length, 1 + x.Length / 160);
        for (int t = 0; t < n; t++)
        {
            // the whole span the frame compares (W + MaxTau) centred on it; the newest frames use what there is
            int start = Math.Clamp(t * 160 - (W + MaxTau) / 2, 0, Math.Max(0, x.Length - W - MaxTau - 1));
            if (start + W + MaxTau >= x.Length) { f0[t] = 0; continue; }
            double energy = 0;
            for (int i = 0; i < W; i++) energy += x[start + i] * x[start + i];
            if (energy / W < 1e-5) { f0[t] = 0; continue; }
            d[0] = 1;
            float run = 0;
            int best = -1;
            var a0 = x.Slice(start, W);
            for (int tau = 1; tau <= MaxTau; tau++)
            {
                float s = SquaredDiff(a0, x.Slice(start + tau, W));
                run += s;
                d[tau] = run > 0 ? s * tau / run : 1;
            }
            int global = MinTau;
            for (int tau = MinTau; tau < MaxTau; tau++)
            {
                if (d[tau] < d[global]) global = tau;
                if (d[tau] < Threshold)
                {
                    while (tau + 1 < MaxTau && d[tau + 1] < d[tau]) tau++;
                    best = tau;
                    break;
                }
            }
            // no dip under the threshold: YIN's fallback, the deepest one, when it is still clearly periodic
            if (best < 1 && d[global] < Fallback) best = global;
            if (best < 1) { f0[t] = 0; continue; }
            float a = d[best - 1], b = d[best], c = d[best + 1];
            float den = a - 2 * b + c, shift = den != 0 ? 0.5f * (a - c) / den : 0;
            f0[t] = 16000f / (best + shift);
        }
        for (int t = n; t < f0.Length; t++) f0[t] = 0;
    }

    private static float SquaredDiff(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(a);
        var vb = System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(b);
        var acc = System.Numerics.Vector<float>.Zero;
        for (int i = 0; i < va.Length; i++) { var d = va[i] - vb[i]; acc += d * d; }
        float s = System.Numerics.Vector.Dot(acc, System.Numerics.Vector<float>.One);
        for (int i = va.Length * System.Numerics.Vector<float>.Count; i < a.Length; i++) { float d = a[i] - b[i]; s += d * d; }
        return s;
    }

    public void Dispose() { }
}

/// <summary>RVC's pitch post-processing: short gaps filled, shifted by semitones, and the 1..255 coarse bins.</summary>
public static class F0
{
    /// <summary>Longest unvoiced gap (frames) that gets an interpolated pitch; longer ones stay unvoiced (RVC fills all: hum in pauses).</summary>
    public const int MaxGap = 15;

    private static readonly double MelMin = 1127 * Math.Log(1 + 50 / 700.0), MelMax = 1127 * Math.Log(1 + 1100 / 700.0);

    /// <summary>In place on <paramref name="f"/>: fills gaps, multiplies by 2^(semis/12); writes the coarse bins.</summary>
    public static void Post(Span<float> f, float semis, Span<long> coarse)
    {
        int last = -1;
        for (int i = 0; i < f.Length; i++)
        {
            if (f[i] <= 0) continue;
            if (last < 0) { if (i <= MaxGap) for (int j = 0; j < i; j++) f[j] = f[i]; }
            else if (i - last - 1 <= MaxGap) for (int j = last + 1; j < i; j++) f[j] = f[last] + (f[i] - f[last]) * (j - last) / (i - last);
            last = i;
        }
        if (last >= 0 && f.Length - last - 1 <= MaxGap) for (int j = last + 1; j < f.Length; j++) f[j] = f[last];
        float k = MathF.Pow(2, semis / 12f);
        for (int i = 0; i < f.Length; i++)
        {
            f[i] *= k;
            coarse[i] = Coarse(f[i]);
        }
    }

    public static long Coarse(float hz)
    {
        double m = 1127 * Math.Log(1 + hz / 700.0);
        if (m > 0) m = (m - MelMin) * 254 / (MelMax - MelMin) + 1;
        return (long)Math.Round(Math.Clamp(m, 1, 255));
    }
}
