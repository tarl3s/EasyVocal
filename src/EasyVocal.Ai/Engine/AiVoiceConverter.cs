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
/// (L = one block + a cushion for the processing time). When that sample isn't there yet (the PC is busy, the
/// stream is starting) it plays the input from that same moment instead (the "dry" voice, time-aligned) with a 5 ms
/// crossfade, starting from the last good audio mirrored (so nothing clicks), and counts an under-run; the cushion
/// then grows by 20 ms (applied while the dry voice plays). When the worker falls more than three blocks behind it
/// skips ahead (an overload). Nothing here allocates, locks or waits on the audio thread.
/// </para>
/// </summary>
public sealed class AiVoiceConverter : IVoiceConverter
{
    private const int Rate = AudioFormat.SampleRate;
    private const int RingBits = 18;                         // 262 144 samples = 5.5 s
    private const int Ring = 1 << RingBits, Mask = Ring - 1;
    private const int Fade = Rate * 5 / 1000;                // 5 ms
    private const int Bump = Rate * 20 / 1000;               // the cushion grows by 20 ms after an under-run
    private const float FadeStep = 1f / Fade;

    private readonly Func<(IBlockConverter Stream, IDisposable? Lease)> makeStream;
    private readonly int blockSamples48;
    private readonly int maxCushion;
    private readonly string provider;
    private readonly float[] inRing = new float[Ring], outRing = new float[Ring];
    private readonly float[] hold = new float[Fade];         // the last wet audio played, to fade out from
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
    private float wetGain;
    private bool hadWet;
    private int holdPos;
    private int replay;
    private long lastWakeBlock = -1;
    private long lastWet = long.MinValue;                     // the last wet position played
    private int shiftBy, shiftLeft;                           // the dry read's crossfade after the cushion grew
    private int dryOffset;                                    // worker → audio: the resamplers' delay + the scheme's (wet lags its position)
    private int resamplerDelay;                               // worker → stats

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
    public AiVoiceConverter(Func<(IBlockConverter Stream, IDisposable? Lease)> makeStream, int blockSamples48, double cushionMs,
        double algorithmicMs, string provider)
    {
        this.makeStream = makeStream;
        this.blockSamples48 = blockSamples48;
        this.provider = provider;
        AlgorithmicSamples = (int)(algorithmicMs * Rate / 1000);
        cushion = Math.Max(Rate * 30 / 1000, (int)(cushionMs * Rate / 1000));
        maxCushion = Math.Max(cushion * 2, Rate * 400 / 1000);
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


    public IVoiceConverter CreateFollower() =>
        new AiVoiceConverter(makeStream, blockSamples48, cushion * 1000.0 / Rate, AlgorithmicSamples * 1000.0 / Rate, provider);

    // ---------------- audio thread ----------------

    public void Process(Span<float> x)
    {
        if (disposed || error != null) return;
        int n = x.Length;
        long t0 = written;

        // the input, for the worker and for the time-aligned dry voice
        int at = (int)(t0 & Mask);
        int first = Math.Min(n, Ring - at);
        x[..first].CopyTo(inRing.AsSpan(at));
        if (first < n) x[first..].CopyTo(inRing);
        Volatile.Write(ref written, t0 + n);
        long blockNo = (t0 + n) / Math.Max(1, blockSamples48 / 4);
        if (blockNo != lastWakeBlock)
        {
            lastWakeBlock = blockNo;
            wake.Set();
        }

        long avail = Volatile.Read(ref produced);
        long from = Volatile.Read(ref producedFrom);
        long valid = Volatile.Read(ref resetAt);
        if (from > valid) valid = from;
        long start = t0 - (blockSamples48 + cushion); // L (the algorithmic part of the delay is inside the audio itself)
        int dryLag = Volatile.Read(ref dryOffset);
        long end = t0 + n;
        for (int i = 0; i < n; i++)
        {
            long p = start + i;
            float dry = Dry(p - dryLag, end);
            if (shiftLeft > 0)
            {
                // the read position just moved back (a bigger cushion): crossfade from where it was
                float g = shiftLeft / (float)Fade;
                dry += (Dry(p - dryLag + shiftBy, end) - dry) * g;
                shiftLeft--;
            }
            bool have = p > lastWet && p >= valid && p < avail && avail - p < Ring; // never the same wet sample twice
            float wet;
            if (have)
            {
                wet = outRing[(int)(p & Mask)];
                lastWet = p;
                hadWet = true;
                replay = 0;
                hold[holdPos] = wet;
                holdPos = holdPos + 1 == Fade ? 0 : holdPos + 1;
                if (wetGain < 1f) { wetGain += FadeStep; if (wetGain > 1f) wetGain = 1f; }
                if (!ready) ready = true;
            }
            else
            {
                if (hadWet)
                {
                    hadWet = false;
                    if (p >= valid) // not a reset: the worker was late
                    {
                        Interlocked.Increment(ref underruns);
                        if (cushion + pendingCushion + Bump <= maxCushion) pendingCushion += Bump;
                    }
                }
                // the last good audio, mirrored (continuous at the seam), while it fades out
                replay++;
                int k = holdPos - replay;
                if (k < 0) k += Fade;
                wet = replay <= Fade ? hold[k] : 0f;
                if (wetGain > 0f) { wetGain -= FadeStep; if (wetGain < 0f) wetGain = 0f; }
            }
            x[i] = dry + (wet - dry) * wetGain;
        }

        if (pendingCushion > 0 && wetGain == 0f)
        {
            // only while the dry voice plays: moving the read position back repeats a little of it (crossfaded)
            cushion += pendingCushion;
            shiftBy = pendingCushion;
            shiftLeft = Fade;
            pendingCushion = 0;
        }
    }

    /// <summary>The input at position <paramref name="p"/> (0 before the start or when it is no longer in the ring).</summary>
    private float Dry(long p, long end) => p >= 0 && end - p < Ring ? inRing[(int)(p & Mask)] : 0f;

    public void Reset()
    {
        if (disposed) return;
        Volatile.Write(ref resetAt, written);
        wetGain = 0f;
        hadWet = false;
        replay = Fade + 1;
        Array.Clear(hold);
        wake.Set();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { wake.Set(); }
        catch (ObjectDisposedException) { }
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
        }
    }

    private void Loop(IBlockConverter stream)
    {
        var down = new PolyResampler(Rate, 16000);
        var up = new PolyResampler(stream.SampleRate, Rate);
        int rd = (int)Math.Round(down.DelayIn + up.DelayIn * Rate / stream.SampleRate);
        Volatile.Write(ref resamplerDelay, rd);
        Volatile.Write(ref dryOffset, rd + AlgorithmicSamples);
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
