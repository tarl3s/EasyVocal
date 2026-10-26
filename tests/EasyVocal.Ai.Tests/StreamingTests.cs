using System.Diagnostics;
using EasyVocal.Ai.Engine;
using EasyVocal.Tests.Helpers;
using Xunit.Abstractions;

namespace EasyVocal.Ai.Tests;

/// <summary>A worker-side stand-in: passes 16 kHz audio through (or a scaled copy), taking as long as told.</summary>
internal sealed class FakeBlocks : IBlockConverter
{
    private readonly Func<int, int> sleepMs;
    private int n;

    public FakeBlocks(int blockMs, Func<int, int>? sleepMs = null, float gain = 1f, int throwAt = -1)
    {
        BlockSamples16 = blockMs * 16;
        this.sleepMs = sleepMs ?? (_ => 0);
        Gain = gain;
        ThrowAt = throwAt;
    }

    public int BlockSamples16 { get; }
    public int BlockSamplesOut => BlockSamples16;
    public int SampleRate => 16000;
    public string PitchName => "fake";
    public float Gain { get; }
    public int ThrowAt { get; }
    public int Steps => Volatile.Read(ref n);
    public int Resets;
    public bool Disposed;

    public void Step(ReadOnlySpan<float> blk16, Span<float> output)
    {
        int i = Interlocked.Increment(ref n) - 1;
        if (i == ThrowAt) throw new InvalidOperationException("boom");
        int ms = sleepMs(i);
        if (ms > 0) Thread.Sleep(ms);
        for (int k = 0; k < blk16.Length; k++) output[k] = blk16[k] * Gain;
    }

    public void Reset() => Interlocked.Increment(ref Resets);
    public void Dispose() => Disposed = true;
}

/// <summary>
/// The audio thread's side of an AI voice (<see cref="AiVoiceConverter"/>) with stand-in workers: the delay is what it
/// says, the audio thread never waits or allocates, a late worker is covered by the time-aligned dry voice without
/// clicks (and counted), a stuck one is skipped past, errors and disposal are harmless.
/// </summary>
[Collection("Timing")]
public class StreamingTests
{
    private const int Sr = 48000, Chunk = 480;
    private readonly ITestOutputHelper output;

    public StreamingTests(ITestOutputHelper output) => this.output = output;

    private static AiVoiceConverter Make(FakeBlocks fake, double cushionMs = 40) =>
        new(() => (fake, null), fake.BlockSamples16 * 3, cushionMs, 0, "CPU");

    /// <summary>A speech-band signal: a few tones plus a slow amplitude wave (no energy above 7 kHz, so 16 kHz keeps it).</summary>
    private static float[] Tones(double seconds)
    {
        var x = new float[(int)(seconds * Sr)];
        for (int i = 0; i < x.Length; i++)
        {
            double t = (double)i / Sr;
            x[i] = (float)(0.25 * (Math.Sin(2 * Math.PI * 220 * t) + 0.5 * Math.Sin(2 * Math.PI * 610 * t) + 0.3 * Math.Sin(2 * Math.PI * 1370 * t))
                           * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 1.3 * t)));
        }
        return x;
    }

    /// <summary>Feeds 10 ms blocks; before each, waits until the worker made what that block will read (no time pressure).</summary>
    private static float[] FeedLockstep(AiVoiceConverter c, float[] input)
    {
        var o = new float[input.Length];
        var buf = new float[Chunk];
        bool stuck = false;
        for (int i = 0; i + Chunk <= input.Length; i += Chunk)
        {
            long need = c.WrittenSamples + Chunk - (c.BlockSamples + c.CushionSamples);
            var sw = Stopwatch.StartNew();
            while (!stuck && c.ProducedSamples < need && c.WrittenSamples >= c.BlockSamples)
            {
                if (sw.ElapsedMilliseconds > 2000 || c.Stats.Error != null || c.IsDisposed) stuck = true; // the worker is gone: stop waiting
                Thread.Sleep(1);
            }
            input.AsSpan(i, Chunk).CopyTo(buf);
            c.Process(buf);
            buf.CopyTo(o, i);
        }
        return o;
    }

    /// <summary>Feeds 10 ms blocks in real time (catching up after a late wake, like a sound card's callback); returns the output and the slowest Process call.</summary>
    internal static (float[] Output, double MaxProcessMs) FeedRealTime(Action<float[]> process, float[] input)
    {
        var o = new float[input.Length];
        var buf = new float[Chunk];
        var clock = Stopwatch.StartNew();
        double max = 0;
        for (int i = 0; i + Chunk <= input.Length; i += Chunk)
        {
            double due = i * 1000.0 / Sr;
            while (clock.Elapsed.TotalMilliseconds < due) Thread.Sleep(1);
            input.AsSpan(i, Chunk).CopyTo(buf);
            long t0 = Stopwatch.GetTimestamp();
            process(buf);
            max = Math.Max(max, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            buf.CopyTo(o, i);
        }
        return (o, max);
    }

    /// <summary>The lag (samples) where <paramref name="y"/> best matches <paramref name="x"/>, and that correlation.</summary>
    internal static (int Lag, double Corr) BestLag(float[] x, float[] y, int maxLag, int from)
    {
        int best = 0;
        double bestC = double.NegativeInfinity;
        int len = Math.Min(x.Length, y.Length) - maxLag - from;
        for (int lag = 0; lag <= maxLag; lag += 1)
        {
            double s = 0, a = 0, b = 0;
            for (int i = from; i < from + len; i += 4)
            {
                double xv = x[i], yv = y[i + lag];
                s += xv * yv; a += xv * xv; b += yv * yv;
            }
            double c = s / Math.Sqrt(a * b + 1e-30);
            if (c > bestC) { bestC = c; best = lag; }
        }
        return (best, bestC);
    }

    [Fact]
    public void The_voice_comes_out_exactly_its_latency_behind()
    {
        var fake = new FakeBlocks(100);
        using var c = Make(fake, cushionMs: 40);
        var x = Tones(3);
        var y = FeedLockstep(c, x);
        var (lag, corr) = BestLag(x, y, Sr * 400 / 1000, Sr / 2);
        int expected = c.LatencySamples; // block + cushion + the resamplers (the stand-in adds nothing of its own)
        output.WriteLine($"latency {c.LatencyMs:0.0} ms ({expected} samples); measured lag {lag} samples ({lag * 1000.0 / Sr:0.0} ms), correlation {corr:0.0000}");
        Assert.True(corr > 0.98, $"correlation {corr}");
        Assert.InRange(lag - expected, -3, 3);
        Assert.Equal(0, c.Stats.Underruns);
        Assert.True(c.Stats.Ready);
    }

    [Fact]
    public void The_audio_thread_never_waits_or_allocates_even_with_a_slow_worker()
    {
        var fake = new FakeBlocks(100, _ => 160); // every block takes longer than it lasts
        using var c = Make(fake, cushionMs: 40);
        var x = Tones(2.5);
        var buf = new float[Chunk];
        for (int i = 0; i < 20; i++) { x.AsSpan(i * Chunk, Chunk).CopyTo(buf); c.Process(buf); } // warm up (JIT)
        var clock = Stopwatch.StartNew();
        double maxMs = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i + Chunk <= x.Length; i += Chunk)
        {
            while (clock.Elapsed.TotalMilliseconds < i * 1000.0 / Sr) Thread.Sleep(1);
            x.AsSpan(i, Chunk).CopyTo(buf);
            long t0 = Stopwatch.GetTimestamp();
            c.Process(buf);
            maxMs = Math.Max(maxMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"slowest Process {maxMs:0.000} ms, allocated {allocated} bytes on the audio thread; {c.Stats}");
        Assert.True(maxMs < (Helpers.RealModels.OnCi ? 25 : 8), $"Process took {maxMs} ms");
        Assert.True(allocated < 2048, $"allocated {allocated} bytes");
        Assert.True(c.Stats.Overloads + c.Stats.Underruns > 0, "the slow worker shows in the stats");
        Assert.All(x, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void A_late_block_is_covered_by_the_dry_voice_without_a_click_and_counted()
    {
        // fast blocks, then one far beyond the cushion
        var fake = new FakeBlocks(100, i => i == 12 ? 350 : 0);
        using var c = Make(fake, cushionMs: 40);
        int cushionBefore = c.CushionSamples;
        var x = Tones(4);
        var (y, _) = FeedRealTime(b => c.Process(b), x);
        double maxJump = 0;
        for (int i = Sr / 2; i < y.Length; i++) maxJump = Math.Max(maxJump, Math.Abs(y[i] - y[i - 1]));
        double sineJump = 0;
        for (int i = 1; i < x.Length; i++) sineJump = Math.Max(sineJump, Math.Abs(x[i] - x[i - 1]));
        var st = c.Stats;
        output.WriteLine($"{st}; cushion {cushionBefore} → {c.CushionSamples}; biggest step {maxJump:0.0000} (the input's own: {sineJump:0.0000})");
        Assert.True(st.Underruns >= 1, "the late block counted");
        Assert.True(c.CushionSamples > cushionBefore, "the cushion grew");
        Assert.True(maxJump < sineJump * 3 + 0.02, $"a click: step {maxJump}");
        Assert.All(y, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void A_worker_that_falls_far_behind_skips_ahead()
    {
        var fake = new FakeBlocks(100, i => i < 2 ? 1200 : 0);
        using var c = Make(fake);
        var (y, _) = FeedRealTime(b => c.Process(b), Tones(5.5));
        var st = c.Stats;
        output.WriteLine(st.ToString());
        Assert.True(st.Overloads >= 1);
        Assert.True(st.Ready, "it caught up and plays the voice again");
        Assert.All(y, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void A_reset_forgets_the_old_stream()
    {
        var fake = new FakeBlocks(100);
        using var c = Make(fake);
        var x = Tones(2);
        FeedLockstep(c, x);
        c.Reset();
        var sw = Stopwatch.StartNew();
        while (fake.Resets == 0 && sw.ElapsedMilliseconds < 2000) Thread.Sleep(1);
        Assert.Equal(1, fake.Resets);
        var y = FeedLockstep(c, x);
        Assert.Equal(0, c.Stats.Underruns); // a reset isn't a late block
        Assert.True(ImportTests.Rms(y.AsSpan(Sr, Sr).ToArray()) > 0.05);
    }

    [Fact]
    public void A_worker_error_leaves_the_voice_untouched_and_is_reported()
    {
        var fake = new FakeBlocks(100, throwAt: 2);
        using var c = Make(fake);
        var x = Tones(2);
        FeedLockstep(c, x);
        var sw = Stopwatch.StartNew();
        while (c.Stats.Error == null && sw.ElapsedMilliseconds < 2000) Thread.Sleep(5);
        Assert.Equal("boom", c.Stats.Error);
        var buf = x.AsSpan(0, Chunk).ToArray();
        c.Process(buf);
        Assert.Equal(x.AsSpan(0, Chunk).ToArray(), buf);
        sw.Restart();
        while (!fake.Disposed && sw.ElapsedMilliseconds < 2000) Thread.Sleep(5);
        Assert.True(fake.Disposed, "the worker cleaned up after itself");
    }

    [Fact]
    public void Disposing_while_the_audio_thread_plays_is_safe()
    {
        var fake = new FakeBlocks(100, _ => 20);
        var c = Make(fake);
        var x = Tones(1.5);
        Exception? failure = null;
        var audio = new Thread(() =>
        {
            try { FeedRealTime(b => c.Process(b), x); }
            catch (Exception ex) { failure = ex; }
        });
        audio.Start();
        Thread.Sleep(500);
        c.Dispose();
        audio.Join();
        Assert.Null(failure);
        var buf = x.AsSpan(0, Chunk).ToArray();
        c.Process(buf);
        Assert.Equal(x.AsSpan(0, Chunk).ToArray(), buf); // after Dispose: untouched
        var sw = Stopwatch.StartNew();
        while (!fake.Disposed && sw.ElapsedMilliseconds < 2000) Thread.Sleep(5);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void A_follower_is_its_own_stream()
    {
        int made = 0;
        var fakes = new List<FakeBlocks>();
        var c = new AiVoiceConverter(() =>
        {
            Interlocked.Increment(ref made);
            var f = new FakeBlocks(100, gain: 0.5f);
            lock (fakes) fakes.Add(f);
            return (f, null);
        }, 4800, 40, 0, "CPU");
        var f2 = (AiVoiceConverter)c.CreateFollower();
        var x = Tones(1.5);
        var y1 = FeedLockstep(c, x);
        var y2 = FeedLockstep(f2, x.Select(v => v * 0.5f).ToArray());
        c.Dispose();
        f2.Dispose();
        Assert.Equal(2, made);
        Assert.Equal(c.LatencySamples, f2.LatencySamples);
        Assert.True(ImportTests.Rms(y1.AsSpan(Sr, Sr / 4).ToArray()) > 1.8 * ImportTests.Rms(y2.AsSpan(Sr, Sr / 4).ToArray()));
    }
}

[CollectionDefinition("Timing", DisableParallelization = true)]
public class TimingCollection { }

