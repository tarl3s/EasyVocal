using System.Diagnostics;
using EasyVocal.Ai.Dsp;
using EasyVocal.Ai.Rvc;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Logging;

namespace EasyVocal.Ai.Engine;

/// <summary>
/// The worker side of a stream: one block of 16 kHz input in, one block of output at <see cref="SampleRate"/> out.
/// <see cref="RvcStream"/> is the real one; the tests use stand-ins.
/// </summary>
public interface IBlockConverter : IDisposable
{
    int BlockSamples16 { get; }
    int BlockSamplesOut { get; }
    int SampleRate { get; }
    string PitchName { get; }
    void Step(ReadOnlySpan<float> blk16, Span<float> output);
    void Reset();
}

/// <summary>What a stream reports (any thread).</summary>
public sealed record AiStreamStats(
    bool Ready, int LatencyMs, int Underruns, int Overloads, int Blocks, double BlockMsP95, double Rtf,
    string Provider, string Pitch, string? Error);

/// <summary>
/// An AI voice in the voice chain. The audio thread (<see cref="Process"/>) only copies: the mic into a ring the
/// worker reads, and out of a ring the worker fills, <see cref="LatencySamples"/> behind. The worker thread runs the
/// model one block at a time (<see cref="RvcStream"/>) with resampling to 16 kHz and back.
/// <para>
/// Every output sample has its input's position: the audio thread asks for position <c>now − L</c>
/// (L = one block + a cushion for the processing time). When that sample isn't there yet (the PC is busy) the gap is
/// covered with the last good converted audio, repeated back and forth from its newest sample (so nothing clicks) and
/// fading over 100 ms into the stand-in voice (<see cref="AiFallback"/>), then a 5 ms crossfade back to the voice; it
/// counts an under-run and the cushion grows by 20 ms. The person's own (dry) voice is never played instead: before
/// the first converted audio, after a worker error and once disposed it is the stand-in voice. When the worker falls
/// more than three blocks behind it skips ahead (an overload). Nothing here allocates, locks or waits on the audio thread.
/// </para>
/// </summary>
public sealed class AiVoiceConverter : IVoiceConverter
{
    private const int Rate = AudioFormat.SampleRate;
    private const int RingBits = 18;                         // 262 144 samples = 5.5 s
    private const int Ring = 1 << RingBits, Mask = Ring - 1;
    private const int Fade = Rate * 5 / 1000;                // 5 ms
    private const int Bump = Rate * 20 / 1000;               // the cushion grows by 20 ms after an under-run
    /// <summary>How much of the last good audio a gap repeats (20 ms).</summary>
    private const int HoldLength = Rate * 20 / 1000;
    private const int HoldRing = 2048, HoldMask = HoldRing - 1; // > HoldLength + Fade: a gap during the fade-in still has its audio
    /// <summary>A gap fades the repeated audio to silence over 100 ms.</summary>
    private const int ConcealLength = Rate * 100 / 1000;
    private const float ConcealStep = 1f / ConcealLength;
    private const int FallbackMax = 2048;
    /// <summary>After 20 s without a gap the cushion gives back 10 ms (down to where it started).</summary>
    private const long DecayAfter = Rate * 20L;
    private const int Shrink = Rate * 10 / 1000;

    private readonly Func<(IBlockConverter Stream, IDisposable? Lease)> makeStream;
    private readonly int blockSamples48;
    private readonly int maxCushion;
    private readonly string provider;
    private readonly float[] inRing = new float[Ring], outRing = new float[Ring];
    private readonly float[] hold = new float[HoldRing];     // the last wet audio played (a gap repeats it)
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread worker;
    private volatile bool disposed;
    private volatile string? error;

    // audio thread → worker
    private long written;                                     // input samples written (absolute position)
    private long resetAt;                                     // where the current stream begins
    // worker → audio thread
    private long produced;                                    // output available up to this position (exclusive)
    private long producedFrom;                                // ...from this position on (the stream's start)

    // audio thread only
    private int cushion;                                      // samples
    private int pendingCushion;
    private long lastWet = long.MinValue;                     // the last wet position played
    private bool concealing = true;                           // no converted audio for this sample: the gap is covered
    private int fadeIn;                                       // samples left of the crossfade from the cover back to the voice
    private long heldCount;                                   // wet samples kept in hold (absolute count)
    private long concealStart, concealEnd, concealAt;         // the held stretch being repeated (absolute counts)
    private int concealDir;
    private float concealGain;
    private int resamplerDelay;                               // worker → stats
    private int dryLag;                                       // worker → audio: the resamplers' delay + the scheme's (the voice lags its position)
    private readonly int startCushion;
    private long sinceUnderrun;                               // wet samples played since the last gap
    private int skipBy, skipLeft;                             // the crossfade after the cushion shrank
    private readonly VoiceShifter shifter = new(Rate);        // the stand-in voice (gaps, the start, a failed worker)
    private readonly float[] fallbackBuf = new float[FallbackMax];
    private readonly AiFallback fallback;

    // stats (written by one thread, read anywhere)
    private int underruns, overloads, blocks;
    private volatile bool ready;
    private readonly double[] blockMs = new double[64];
    private int blockMsCount;
    private double blockSeconds;
    private string pitchName = "";

    /// <param name="makeStream">Makes the stream (on the worker thread) and a lease on the shared models it uses.</param>
    /// <param name="blockSamples48">One block at 48 kHz.</param>
    /// <param name="cushionMs">The expected processing time of one block (95th percentile) plus a margin.</param>
    /// <param name="algorithmicMs">What the scheme adds on top (crossfade + search, resamplers).</param>
    /// <param name="fallback">The stand-in voice played where no converted audio is (null = <see cref="AiFallback.Default"/>).</param>
    public AiVoiceConverter(Func<(IBlockConverter Stream, IDisposable? Lease)> makeStream, int blockSamples48, double cushionMs,
        double algorithmicMs, string provider, AiFallback? fallback = null)
    {
        this.makeStream = makeStream;
        this.fallback = fallback ?? AiFallback.Default;
        shifter.PitchSemitones = this.fallback.Semitones;
        shifter.FormantFactor = this.fallback.Formant;
        this.blockSamples48 = blockSamples48;
        this.provider = provider;
        AlgorithmicSamples = (int)(algorithmicMs * Rate / 1000);
        cushion = Math.Max(Rate * 30 / 1000, (int)(cushionMs * Rate / 1000));
        maxCushion = Math.Max(cushion * 2, Rate * 400 / 1000);
        startCushion = cushion;
        blockSeconds = blockSamples48 / (double)Rate;
        worker = new Thread(Work) { IsBackground = true, Name = "EasyVocal AI voice" };
        try { worker.Priority = ThreadPriority.AboveNormal; }
        catch (Exception) { /* not allowed here: normal priority */ }
        worker.Start();
    }

    /// <summary>The crossfade + search part of the delay (fixed by the level).</summary>
    public int AlgorithmicSamples { get; }

    public int LatencySamples => blockSamples48 + Volatile.Read(ref cushion) + AlgorithmicSamples + Volatile.Read(ref resamplerDelay);

    public double LatencyMs => LatencySamples * 1000.0 / Rate;

    /// <summary>The cushion (processing time allowance) in use, in samples.</summary>
    public int CushionSamples => Volatile.Read(ref cushion);

    public bool IsDisposed => disposed;

    /// <summary>Converted audio has been played (any thread).</summary>
    public bool Ready => ready;

    /// <summary>Why the worker stopped (null while it runs).</summary>
    public string? Error => error;

    /// <summary>Tests: input written so far / output available up to (absolute positions).</summary>
    internal long WrittenSamples => Volatile.Read(ref written);
    internal long ProducedSamples => Volatile.Read(ref produced);
    internal int BlockSamples => blockSamples48;

    public AiStreamStats Stats
    {
        get
        {
            double p95;
            lock (blockMs) p95 = P95();
            return new AiStreamStats(ready, (int)Math.Round(LatencyMs), Volatile.Read(ref underruns), Volatile.Read(ref overloads),
                Volatile.Read(ref blocks), p95, blockSeconds > 0 ? p95 / 1000 / blockSeconds : 0, provider, pitchName, error);
        }
    }

    private double P95()
    {
        int n = Math.Min(blockMsCount, blockMs.Length);
        if (n == 0) return 0;
        Span<double> s = stackalloc double[n];
        blockMs.AsSpan(0, n).CopyTo(s);
        s.Sort();
        return s[Math.Min(n - 1, (int)Math.Ceiling(n * 0.95) - 1)];
    }

    /// <summary>
    /// Waits until the worker has stopped (after <see cref="Dispose"/>): only then may the models it runs be closed.
    /// False when it is still inside a model run after <paramref name="timeout"/>.
    /// </summary>
    public bool WaitStopped(TimeSpan timeout) => worker.Join(timeout);

    // ---------------- audio thread ----------------

    public void Process(Span<float> x)
    {
        // Disposed: the worker is gone and nothing new is converted, so what plays is the stand-in voice (never the input).
        for (int i = 0; i < x.Length; i += FallbackMax) ProcessPart(x.Slice(i, Math.Min(FallbackMax, x.Length - i)));
    }

    private void ProcessPart(Span<float> x)
    {
        int n = x.Length;
        long t0 = written;

        // the input, for the worker (and the stand-in voice)
        int at = (int)(t0 & Mask);
        int first = Math.Min(n, Ring - at);
        x[..first].CopyTo(inRing.AsSpan(at));
        if (first < n) x[first..].CopyTo(inRing);
        Volatile.Write(ref written, t0 + n);
        if (!disposed)
        {
            try { wake.Set(); }
            catch (ObjectDisposedException) { } // the worker is gone (disposed meanwhile): the stand-in voice plays below
        }

        long avail = Volatile.Read(ref produced);
        long from = Volatile.Read(ref producedFrom);
        long valid = Volatile.Read(ref resetAt);
        if (from > valid) valid = from;
        long start = t0 - (blockSamples48 + cushion); // L (the algorithmic part of the delay is inside the audio itself)

        // The stand-in voice: the input from the same moment as the converted audio, shifted towards the AI voice.
        var fb = fallbackBuf.AsSpan(0, n);
        long dryAt = start - Volatile.Read(ref dryLag) + shifter.LatencySamples;
        long end = t0 + n;
        for (int i = 0; i < n; i++)
        {
            long q = dryAt + i;
            fb[i] = q >= 0 && q < end && end - q < Ring ? inRing[(int)(q & Mask)] : 0f;
        }
        shifter.Process(fb);

        for (int i = 0; i < n; i++)
        {
            long p = start + i;
            bool have = p > lastWet && p >= valid && p < avail && avail - p < Ring; // never the same wet sample twice
            if (have)
            {
                float wet = outRing[(int)(p & Mask)];
                if (skipLeft > 0)
                {
                    // the cushion just shrank: crossfade from where the read was
                    long old = p - skipBy;
                    if (old >= valid && old < avail) wet += (outRing[(int)(old & Mask)] - wet) * (skipLeft / (float)Fade);
                    skipLeft--;
                }
                lastWet = p;
                if (concealing)
                {
                    concealing = false;
                    fadeIn = Fade; // from the cover back to the voice
                }
                if (fadeIn > 0)
                {
                    float c = Cover(fb[i]);
                    x[i] = c + (wet - c) * (1f - fadeIn / (float)Fade);
                    fadeIn--;
                }
                else x[i] = wet;
                hold[(int)(heldCount & HoldMask)] = wet;
                heldCount++;
                sinceUnderrun++;
                if (!ready) ready = true;
            }
            else
            {
                if (!concealing)
                {
                    concealing = true;
                    fadeIn = 0;
                    if (heldCount > 0 && p >= valid) // the worker was late (not a reset or the stream's start)
                    {
                        Interlocked.Increment(ref underruns);
                        sinceUnderrun = 0;
                        if (cushion + pendingCushion + Bump <= maxCushion) pendingCushion += Bump;
                    }
                    StartConceal();
                }
                x[i] = Cover(fb[i]);
            }
        }

        if (pendingCushion > 0 && concealing)
        {
            // while the gap is covered: reading further back makes the gap 20 ms longer, never repeats the voice
            cushion += pendingCushion;
            pendingCushion = 0;
        }
        else if (!concealing && skipLeft == 0 && sinceUnderrun > DecayAfter && cushion - Shrink >= startCushion)
        {
            // a long time without a gap: give back 10 ms of delay (crossfaded)
            cushion -= Shrink;
            skipBy = Shrink;
            skipLeft = Fade;
            sinceUnderrun = 0;
        }
    }

    /// <summary>
    /// What a gap plays: the last good audio repeated (see <see cref="StartConceal"/>), fading over 100 ms into the
    /// stand-in voice (the input shifted towards the AI voice) — never the person's own voice.
    /// </summary>
    private float Cover(float standIn)
    {
        float g = concealGain > 0f ? concealGain : 0f;
        return NextConceal() + standIn * (1f - g);
    }

    /// <summary>
    /// The gap starts: repeat the last good audio (the last <see cref="HoldLength"/> samples, back and forth from the
    /// newest one, so it is continuous at every turn) while it fades out over <see cref="ConcealLength"/>.
    /// Nothing held (the stream's start, a reset) = the stand-in voice at once.
    /// </summary>
    private void StartConceal()
    {
        long count = Math.Min(heldCount, HoldLength);
        concealEnd = heldCount - 1;
        concealStart = heldCount - count;
        concealAt = Math.Max(concealStart, concealEnd - 1);
        concealDir = -1;
        concealGain = count > 1 ? 1f : 0f;
    }

    private float NextConceal()
    {
        if (concealGain <= 0f) return 0f;
        float v = hold[(int)(concealAt & HoldMask)] * concealGain;
        concealAt += concealDir;
        if (concealAt < concealStart) { concealAt = Math.Min(concealEnd, concealStart + 1); concealDir = 1; }
        else if (concealAt > concealEnd) { concealAt = Math.Max(concealStart, concealEnd - 1); concealDir = -1; }
        concealGain -= ConcealStep;
        return v;
    }

    public void Reset()
    {
        if (disposed) return;
        Volatile.Write(ref resetAt, written);
        concealing = true; // a new stream: the stand-in voice until it plays (the old audio isn't repeated)
        concealGain = 0f;
        fadeIn = 0;
        heldCount = 0;
        skipLeft = 0;
        shifter.Reset();
        try { wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { wake.Set(); }
        catch (ObjectDisposedException) { }
        if (!worker.IsAlive) { try { wake.Dispose(); } catch (Exception) { } }
    }

    // ---------------- worker ----------------

    private void Work()
    {
        IBlockConverter? stream = null;
        IDisposable? lease = null;
        try
        {
            (stream, lease) = makeStream();
            pitchName = stream.PitchName;
            Loop(stream);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Error("AI voice stopped", ex);
        }
        finally
        {
            try { stream?.Dispose(); } catch (Exception) { }
            try { lease?.Dispose(); } catch (Exception) { }
            if (disposed) wake.Dispose(); // only once nobody wakes it any more (the audio thread checks disposed first; a late Set is caught)
        }
    }

    private void Loop(IBlockConverter stream)
    {
        var down = new PolyResampler(Rate, 16000);
        var up = new PolyResampler(stream.SampleRate, Rate);
        int rd = (int)Math.Round(down.DelayIn + up.DelayIn * Rate / stream.SampleRate);
        Volatile.Write(ref resamplerDelay, rd);
        Volatile.Write(ref dryLag, rd + AlgorithmicSamples);
        int b16 = stream.BlockSamples16;
        int bOut = stream.BlockSamplesOut;
        var fifo = new float[b16 * 4];
        int fifoCount = 0;
        var chunk = new float[4096];
        var chunk16 = new float[down.MaxOutput(chunk.Length)];
        var outSr = new float[bOut];
        var out48 = new float[up.MaxOutput(bOut)];
        long seenReset = Volatile.Read(ref resetAt);
        long streamStart = seenReset;
        long consumed = streamStart, outPos = streamStart;
        Volatile.Write(ref producedFrom, streamStart);
        Volatile.Write(ref produced, streamStart);
        long overloadAt = 3L * blockSamples48 + Rate / 2;

        while (!disposed)
        {
            long r = Volatile.Read(ref resetAt);
            long w = Volatile.Read(ref written);
            if (r != seenReset || w - consumed > overloadAt)
            {
                if (r == seenReset)
                {
                    Interlocked.Increment(ref overloads); // too far behind: start again from (almost) now
                    r = w - blockSamples48 / 2;
                }
                else seenReset = r;
                streamStart = r;
                consumed = outPos = r;
                stream.Reset();
                down.Reset();
                up.Reset();
                fifoCount = 0;
                Volatile.Write(ref producedFrom, r);
                Volatile.Write(ref produced, r);
                continue;
            }
            long avail = w - consumed;
            if (avail <= 0)
            {
                wake.WaitOne(20);
                continue;
            }
            while (avail > 0)
            {
                int take = (int)Math.Min(avail, chunk.Length);
                int pos = (int)(consumed & Mask);
                int a = Math.Min(take, Ring - pos);
                inRing.AsSpan(pos, a).CopyTo(chunk);
                if (a < take) inRing.AsSpan(0, take - a).CopyTo(chunk.AsSpan(a));
                consumed += take;
                avail -= take;
                int got = down.Process(chunk.AsSpan(0, take), chunk16);
                int g = 0;
                while (g < got)
                {
                    int room = Math.Min(got - g, fifo.Length - fifoCount);
                    chunk16.AsSpan(g, room).CopyTo(fifo.AsSpan(fifoCount));
                    fifoCount += room;
                    g += room;
                    while (fifoCount >= b16)
                    {
                        long t = Stopwatch.GetTimestamp();
                        stream.Step(fifo.AsSpan(0, b16), outSr);
                        double ms = (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;
                        Array.Copy(fifo, b16, fifo, 0, fifoCount - b16);
                        fifoCount -= b16;
                        int n48 = up.Process(outSr, out48);
                        if (Volatile.Read(ref resetAt) != seenReset || disposed) break; // a reset meanwhile: drop it
                        WriteOut(out48.AsSpan(0, n48), outPos);
                        outPos += n48;
                        Volatile.Write(ref produced, outPos);
                        Record(ms);
                    }
                    if (Volatile.Read(ref resetAt) != seenReset || disposed) break;
                }
                if (Volatile.Read(ref resetAt) != seenReset || disposed) break;
            }
        }
    }

    private void WriteOut(ReadOnlySpan<float> y, long pos)
    {
        int at = (int)(pos & Mask);
        int a = Math.Min(y.Length, Ring - at);
        y[..a].CopyTo(outRing.AsSpan(at));
        if (a < y.Length) y[a..].CopyTo(outRing);
    }

    private void Record(double ms)
    {
        lock (blockMs)
        {
            blockMs[blockMsCount % blockMs.Length] = ms;
            blockMsCount++;
        }
        Interlocked.Increment(ref blocks);
    }
}

/// <summary>
/// The stand-in voice an AI voice plays where it has no converted audio (a gap the cushion couldn't cover, the
/// stream's start, a worker that failed): the person's voice shifted towards the AI voice, so their own voice never
/// reaches the call while an AI voice is picked.
/// </summary>
public sealed record AiFallback(double Semitones, double Formant)
{
    /// <summary>A few semitones up with a lighter voice body: never the voice as it is.</summary>
    public static AiFallback Default { get; } = new(StandInVoice.DefaultSemitones, StandInVoice.DefaultFormant);

    /// <summary>
    /// Towards a voice's natural pitch (<paramref name="targetHz"/>) from an average speaking voice (140 Hz), plus its
    /// key; at least 3 semitones either way, at most 9, the voice body following the pitch.
    /// </summary>
    public static AiFallback Towards(double? targetHz, double key)
    {
        double semis = key + (targetHz is > 40 and < 1000 ? 12 * Math.Log2(targetHz.Value / 140.0) : 0);
        if (Math.Abs(semis) < 3) semis = semis < 0 ? -3 : 3;
        semis = Math.Clamp(semis, -9, 9);
        return new AiFallback(semis, semis > 0 ? 1.12 : 0.88);
    }
}
