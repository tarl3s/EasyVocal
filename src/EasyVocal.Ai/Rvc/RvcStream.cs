using EasyVocal.Ai.Runtime;

namespace EasyVocal.Ai.Rvc;

/// <summary>Everything one AI voice needs to run: the shared models, its generator and its speaker.</summary>
public sealed class RvcVoice
{
    public RvcVoice(OnnxModel content, RvcGenerator generator, float[]? speaker, OnnxModel? rmvpe, FeatureIndex? index)
    {
        Content = content;
        Generator = generator;
        Speaker = speaker;
        RmvpeModel = rmvpe;
        Index = index;
    }

    public OnnxModel Content { get; }
    public RvcGenerator Generator { get; }
    /// <summary>The speaker vector (256) for the two-graph layout; null = the single graph's speaker 0.</summary>
    public float[]? Speaker { get; }
    /// <summary>RMVPE, when the pack has it (else YIN is used at every level).</summary>
    public OnnxModel? RmvpeModel { get; }
    public FeatureIndex? Index { get; }

    /// <summary>The voice's natural pitch: the input is moved towards it (null = only <see cref="Key"/>).</summary>
    public double? TargetHz { get; init; }

    /// <summary>Semitones added on top (the person's fine-tune, or the whole shift when there is no target).</summary>
    public double Key { get; init; }
}

/// <summary>How a stream runs (what a quality level sets).</summary>
public sealed record StreamSettings(int BlockMs, int ExtraMs, int CrossfadeMs, bool UseRmvpe, float IndexRate)
{
    public const int SearchMs = 10;

    /// <summary>Delay the scheme itself adds on top of the block being collected: crossfade + SOLA search.</summary>
    public int AlgorithmicMs => CrossfadeMs + SearchMs;
}

/// <summary>
/// Follows the speaker's pitch so a voice with a natural pitch (<see cref="RvcVoice.TargetHz"/>) gets the input moved
/// there: the median of the last ~6 s of voiced frames, an assumed 150 Hz until 0.3 s of voicing was heard;
/// then the shift follows it, quickly for a big change (another speaker), half a semitone per block at most otherwise. Allocation-free.
/// </summary>
public sealed class AutoKey
{
    private const int History = 600, MinVoiced = 30;
    private const double PriorHz = 150;
    private readonly float[] ring = new float[History], sort = new float[History];
    private int count, pos;
    private bool known;
    private double key;

    public AutoKey(double? targetHz, double offset)
    {
        TargetHz = targetHz;
        Offset = offset;
        key = Compute(PriorHz);
    }

    public double? TargetHz { get; }
    public double Offset { get; set; }

    /// <summary>The input's typical pitch so far (Hz), or 0 when not known yet.</summary>
    public double InputHz { get; private set; }

    public void Reset()
    {
        count = pos = 0;
        known = false;
        InputHz = 0;
        key = Compute(PriorHz);
    }

    /// <summary>The semitones to shift by after hearing these raw pitch frames.</summary>
    public float Update(ReadOnlySpan<float> f0)
    {
        if (TargetHz is not double) return (float)Offset;
        foreach (float f in f0)
        {
            if (f <= 40 || f >= 1200) continue;
            ring[pos] = MathF.Log2(f);
            pos = (pos + 1) % History;
            if (count < History) count++;
        }
        if (count < MinVoiced) return (float)key;
        Array.Copy(ring, sort, count);
        Array.Sort(sort, 0, count);
        double hz = Math.Pow(2, sort[count / 2]);
        InputHz = hz;
        double want = Compute(hz);
        double diff = want - key;
        key = !known ? want : key + (Math.Abs(diff) > 2 ? diff * 0.6 : Math.Clamp(diff, -0.5, 0.5)); // a new speaker: catch up fast; else drift slowly
        known = true;
        return (float)key;
    }

    private double Compute(double inputHz) =>
        TargetHz is double t ? Math.Clamp(12 * Math.Log2(t / inputHz), -24, 24) + Offset : Offset;
}

/// <summary>
/// RVC's realtime scheme (RVC-Project rtrvc.py) on one stream, run by the worker: a rolling 16 kHz context
/// (extra + crossfade + search + block), HuBERT over all of it, pitch on the newest part only (cached), the generator
/// decoding just the newest block + crossfade + search frames, an output gate driven by the input level, then SOLA
/// (best offset in 10 ms) and a sin² crossfade with the previous block's tail. Allocation-free per block (the
/// single-graph layout aside).
/// </summary>
public sealed class RvcStream : Engine.IBlockConverter
{
    private readonly RvcVoice voice;
    private readonly IPitchEstimator pitch;
    private readonly ContentEncoder content;
    private readonly RvcGenerator.Run run;
    private readonly int bF, cF, sF, P, m, hop;
    private readonly float[] ctx;
    private readonly float[] rawF0, postF0;
    private readonly long[] postCoarse;
    private readonly float[] sola, fadeIn;
    private readonly float[] gateG, gateH;
    private readonly int pitchWin;
    private bool first = true;

    public RvcStream(RvcVoice voice, StreamSettings s, int seed = 1234)
    {
        this.voice = voice;
        Settings = s;
        bF = s.BlockMs / 10;
        int xF = s.ExtraMs / 10;
        cF = s.CrossfadeMs / 10;
        sF = StreamSettings.SearchMs / 10;
        P = xF + cF + sF + bF;
        m = bF + cF + sF;
        hop = voice.Generator.Hop;
        ctx = new float[P * 160];
        content = new ContentEncoder(voice.Content) { Index = voice.Index, IndexRate = voice.Index != null ? s.IndexRate : 0 };
        pitch = s.UseRmvpe && voice.RmvpeModel != null ? new Rmvpe(voice.RmvpeModel) : new Yin();
        pitchWin = Math.Min(pitch.WindowFor(bF * 160 + 800), ctx.Length);
        int pf = 1 + pitchWin / 160;
        rawF0 = new float[pf];
        postF0 = new float[pf];
        postCoarse = new long[pf];
        run = voice.Generator.NewRun(P, m, seed);
        if (voice.Speaker != null) voice.Speaker.AsSpan(0, Math.Min(voice.Speaker.Length, run.Speaker.Length)).CopyTo(run.Speaker);
        int sbf = cF * hop;
        sola = new float[sbf];
        fadeIn = new float[sbf];
        for (int i = 0; i < sbf; i++)
        {
            double sn = Math.Sin(0.5 * Math.PI * i / Math.Max(1, sbf - 1));
            fadeIn[i] = (float)(sn * sn);
        }
        gateG = new float[m];
        gateH = new float[m];
        Key = new AutoKey(voice.TargetHz, voice.Key);
    }

    public StreamSettings Settings { get; }
    public AutoKey Key { get; }
    public string PitchName => pitch.Name;

    /// <summary>16 kHz samples one step takes.</summary>
    public int BlockSamples16 => bF * 160;

    /// <summary>Samples one step gives, at <see cref="SampleRate"/>.</summary>
    public int BlockSamplesOut => bF * hop;

    public int SampleRate => voice.Generator.SampleRate;

    /// <summary>Output gate threshold (dB of the input): frames quieter than this fade out over 10 dB.</summary>
    public float GateDb { get; set; } = -50;

    /// <summary>Back to silence: the next step starts like the first.</summary>
    public void Reset()
    {
        Array.Clear(ctx);
        Array.Clear(sola);
        Array.Clear(run.F0);
        Array.Clear(run.Coarse);
        first = true;
        Key.Reset();
    }

    /// <summary>One block: <see cref="BlockSamples16"/> new input samples → <see cref="BlockSamplesOut"/> output samples.</summary>
    public void Step(ReadOnlySpan<float> blk16, Span<float> output)
    {
        int n = blk16.Length;
        if (n != BlockSamples16 || output.Length < BlockSamplesOut) throw new ArgumentException("Wrong block size.");
        Array.Copy(ctx, n, ctx, 0, ctx.Length - n);
        blk16.CopyTo(ctx.AsSpan(ctx.Length - n));

        content.Run(ctx, run.Phone, P - m);

        // pitch on the newest block + 50 ms (RMVPE: rounded up to its 5120-sample chunks, as RVC does)
        pitch.Estimate(ctx.AsSpan(ctx.Length - pitchWin), rawF0);
        int pf = rawF0.Length;
        float semis = Key.Update(rawF0.AsSpan(Math.Max(0, pf - 1 - bF), Math.Min(bF, pf - 1)));
        rawF0.CopyTo(postF0, 0);
        F0.Post(postF0, semis, postCoarse);
        var f0c = run.F0;
        var cc = run.Coarse;
        Array.Copy(f0c, bF, f0c, 0, P - bF);
        Array.Copy(cc, bF, cc, 0, P - bF);
        int keep = Math.Min(pf - 4, P);                          // pitch[3:-1] → the last (len-4) cache slots
        if (first)
        {
            for (int i = 0; i < P; i++) { f0c[i] = postF0[3]; cc[i] = postCoarse[3]; }
            first = false;
        }
        for (int i = 0; i < keep; i++) { f0c[P - keep + i] = postF0[3 + i]; cc[P - keep + i] = postCoarse[3 + i]; }

        run.Generate();
        var y = run.Audio;
        Gate(y, P - m);

        // SOLA: best offset in [0, search) against the previous tail, then crossfade
        int sbf = cF * hop, ssf = sF * hop, bfr = bF * hop;
        int off = 0;
        if (sbf > 0)
        {
            double best = double.NegativeInfinity;
            for (int o = 0; o <= ssf; o++)
            {
                double num = 0, den = 1e-8;
                for (int i = 0; i < sbf; i++) { float v = y[o + i]; num += v * sola[i]; den += v * v; }
                double score = num / Math.Sqrt(den);
                if (score > best) { best = score; off = o; }
            }
            for (int i = 0; i < sbf; i++) y[off + i] = y[off + i] * fadeIn[i] + sola[i] * (1 - fadeIn[i]);
            Array.Copy(y, off + bfr, sola, 0, sbf);
        }
        y.AsSpan(off, bfr).CopyTo(output);
    }

    /// <summary>
    /// Output gate driven by the input level (what RVC's threshold + RMS mix do): frames whose input is below
    /// <see cref="GateDb"/> fade out over 10 dB, held ±2 frames so word edges aren't clipped. Kills the generator's hum
    /// and breath noise in pauses.
    /// </summary>
    private void Gate(float[] y, int firstFrame)
    {
        if (GateDb <= -120) return;
        int frames = m;
        for (int i = 0; i < frames; i++)
        {
            int s = (firstFrame + i) * 160;
            double e = 0;
            int cnt = 0;
            for (int k = s; k < s + 160 && k < ctx.Length; k++) { if (k >= 0) { e += ctx[k] * ctx[k]; cnt++; } }
            double db = 10 * Math.Log10(e / Math.Max(1, cnt) + 1e-12);
            double t = Math.Clamp((db - (GateDb - 10)) / 10, 0, 1);
            gateG[i] = (float)(t * t * (3 - 2 * t));
        }
        for (int i = 0; i < frames; i++)
        {
            float mx = 0;
            for (int k = Math.Max(0, i - 2); k <= Math.Min(frames - 1, i + 2); k++) mx = Math.Max(mx, gateG[k]);
            gateH[i] = mx;
        }
        int len = Math.Min(y.Length, frames * hop);
        for (int i = 0; i < len; i++)
        {
            double fpos = (double)i / hop - 0.5;
            int a = (int)Math.Floor(fpos);
            double fr = fpos - a;
            float ga = gateH[Math.Clamp(a, 0, frames - 1)], gb = gateH[Math.Clamp(a + 1, 0, frames - 1)];
            y[i] *= (float)(ga + (gb - ga) * fr);
        }
    }

    public void Dispose()
    {
        run.Dispose();
        content.Dispose();
        pitch.Dispose();
    }
}
