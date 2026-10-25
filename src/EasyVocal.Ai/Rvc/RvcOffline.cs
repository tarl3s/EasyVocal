using EasyVocal.Ai.Dsp;

namespace EasyVocal.Ai.Rvc;

/// <summary>
/// A whole recording through a voice at once (best quality: the whole sentence is the context). 48 kHz in and out,
/// same length, aligned. For the tests, references and (later) the sound editor.
/// </summary>
public static class RvcOffline
{
    /// <param name="useRmvpe">RMVPE when the voice has it; else YIN.</param>
    /// <param name="indexRate">The .index blend when the voice has one.</param>
    public static float[] Convert(RvcVoice voice, ReadOnlySpan<float> x48, bool useRmvpe = true, float indexRate = 0.75f,
        float gateDb = -50, int seed = 1234)
    {
        var x = PolyResampler.Convert(x48, 48000, 16000);
        // reflect-pad 0.5 s like RVC's pipeline, convert in one go, cut the pad off again
        int pad = 8000;
        int len = (x.Length + 2 * pad) / 160 * 160;
        var xp = new float[len];
        for (int i = 0; i < xp.Length; i++)
        {
            int j = i - pad;
            if (j < 0) j = -j;
            if (j >= x.Length) j = 2 * (x.Length - 1) - j;
            xp[i] = x.Length == 0 ? 0 : x[Math.Clamp(j, 0, x.Length - 1)];
        }
        int T = xp.Length / 160;
        var gen = voice.Generator;
        using var run = gen.NewRun(T, T, seed);
        if (voice.Speaker != null) voice.Speaker.AsSpan(0, Math.Min(voice.Speaker.Length, run.Speaker.Length)).CopyTo(run.Speaker);
        using (var content = new ContentEncoder(voice.Content) { Index = voice.Index, IndexRate = voice.Index != null ? indexRate : 0 })
            content.Run(xp, run.Phone);
        using IPitchEstimator pitch = useRmvpe && voice.RmvpeModel != null ? new Rmvpe(voice.RmvpeModel) : new Yin();
        var raw = new float[1 + xp.Length / 160];
        pitch.Estimate(xp, raw);
        var key = new AutoKey(voice.TargetHz, voice.Key);
        float semis = key.Update(raw);
        var f0 = run.F0;
        Array.Copy(raw, f0, Math.Min(T, raw.Length));
        F0.Post(f0, semis, run.Coarse);
        run.Generate();
        var y = run.Audio;
        Gate(y, gen.Hop, xp, gateDb);
        int padOut = pad * gen.SampleRate / 16000;
        int core = (int)Math.Min(Math.Max(0, y.Length - 2 * padOut), (long)x.Length * gen.SampleRate / 16000);
        var o = PolyResampler.Convert(y.AsSpan(padOut, core), gen.SampleRate, 48000);
        var result = new float[x48.Length];
        Array.Copy(o, result, Math.Min(o.Length, result.Length));
        return result;
    }

    private static void Gate(float[] y, int hop, float[] x16, float gateDb)
    {
        if (gateDb <= -120) return;
        int frames = x16.Length / 160;
        var g = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double e = 0;
            for (int k = i * 160; k < i * 160 + 160; k++) e += x16[k] * x16[k];
            double db = 10 * Math.Log10(e / 160 + 1e-12);
            double t = Math.Clamp((db - (gateDb - 10)) / 10, 0, 1);
            g[i] = (float)(t * t * (3 - 2 * t));
        }
        var h = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float mx = 0;
            for (int k = Math.Max(0, i - 2); k <= Math.Min(frames - 1, i + 2); k++) mx = Math.Max(mx, g[k]);
            h[i] = mx;
        }
        for (int i = 0; i < y.Length; i++)
        {
            double fpos = (double)i / hop - 0.5;
            int a = (int)Math.Floor(fpos);
            double fr = fpos - a;
            float ga = h[Math.Clamp(a, 0, frames - 1)], gb = h[Math.Clamp(a + 1, 0, frames - 1)];
            y[i] *= (float)(ga + (gb - ga) * fr);
        }
    }
}
