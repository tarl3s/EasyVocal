using System.Diagnostics;
using EasyVocal.Ai.Rvc;
using EasyVocal.Ai.Runtime;
using EasyVocal.Core.Model;

namespace EasyVocal.Ai;

/// <summary>One level measured on one device.</summary>
public sealed record BenchmarkRun(AiQuality Quality, string Provider, int BlockMs, double MeanMs, double P95Ms, double Rtf, double LatencyMs, bool RealTime);

/// <summary>What the benchmark found and suggests.</summary>
public sealed record BenchmarkResult(AiQuality Suggested, AiDevice Device, string Provider, bool AnyRealTime, IReadOnlyList<BenchmarkRun> Runs)
{
    /// <summary>Saves the suggestion and the measured block times into the settings.</summary>
    public void SaveTo(AiSettings s)
    {
        s.SuggestedQuality = AiLevels.Id(Suggested);
        s.SuggestedDevice = Device == AiDevice.Cpu ? "cpu" : "gpu";
        s.BenchmarkedUtc = DateTime.UtcNow;
        foreach (var r in Runs) s.MeasuredBlockMs[AiLevels.Id(r.Quality) + ":" + r.Provider] = Math.Round(r.P95Ms, 1);
    }
}

/// <summary>
/// The one-time measurement that suggests a level for this PC: each level on the processor (and on the GPU when one
/// works), a few seconds of speech-like sound streamed exactly as the voice chain does, the 95th percentile of the
/// block time. A level is real time when that stays under 70 % of a block (room for the rest of the PC). The
/// suggestion is the best level that is real time; the GPU wins a tie (it leaves the processor free).
/// </summary>
public static class AiBenchmark
{
    public const double RealTimeShare = 0.7;

    public static Task<BenchmarkResult> RunAsync(AiVoiceService service, string voiceId, IProgress<string>? progress = null,
        CancellationToken ct = default, int blocks = 12) =>
        Task.Run(() => Run(service, voiceId, progress, ct, blocks), ct);

    public static BenchmarkResult Run(AiVoiceService service, string voiceId, IProgress<string>? progress, CancellationToken ct, int blocks = 12)
    {
        var devices = new List<AiDevice> { AiDevice.Cpu };
        if (OrtRuntime.TryLoad(out _) && OrtRuntime.GpuProvider() != null) devices.Add(AiDevice.Gpu);
        var runs = new List<BenchmarkRun>();
        foreach (var device in devices)
        {
            foreach (var q in AiLevels.All)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"{AiLevels.Id(q)} on the {(device == AiDevice.Cpu ? "processor" : "graphics card")}…");
                BenchmarkRun r;
                try { r = Measure(service, voiceId, q, device, blocks, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Core.Logging.AppLog.Error($"AI benchmark: {q} on {device} failed", ex);
                    break;
                }
                if (device == AiDevice.Gpu && r.Provider == "CPU") break; // the GPU refused: already measured on the CPU
                runs.Add(r);
                if (r.Rtf > 1.0) break; // the next levels are heavier still
            }
        }
        BenchmarkRun? best = null;
        foreach (var r in runs.Where(r => r.RealTime))
            if (best == null || r.Quality > best.Quality || (r.Quality == best.Quality && r.Provider != "CPU" && best.Provider == "CPU")) best = r;
        if (best == null)
        {
            var light = runs.Where(r => r.Quality == AiQuality.Light).OrderBy(r => r.Rtf).FirstOrDefault();
            return new BenchmarkResult(AiQuality.Light, light?.Provider is null or "CPU" ? AiDevice.Cpu : AiDevice.Gpu, light?.Provider ?? "CPU", false, runs);
        }
        return new BenchmarkResult(best.Quality, best.Provider == "CPU" ? AiDevice.Cpu : AiDevice.Gpu, best.Provider, true, runs);
    }

    /// <summary>Streams <paramref name="blocks"/> blocks of speech-like sound through one level and times them.</summary>
    public static BenchmarkRun Measure(AiVoiceService service, string voiceId, AiQuality q, AiDevice device, int blocks, CancellationToken ct = default)
    {
        using var loaded = service.Load(voiceId, q, device, ct);
        var s = loaded.Settings;
        using var stream = new RvcStream(loaded.Voice, s);
        var input = SpeechLike(16000, (blocks + 2) * s.BlockMs / 1000.0 + 0.5);
        int b16 = stream.BlockSamples16;
        var outp = new float[stream.BlockSamplesOut];
        var ms = new List<double>();
        for (int i = 0; i < blocks + 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            long t0 = Stopwatch.GetTimestamp();
            stream.Step(input.AsSpan(i * b16, b16), outp);
            double t = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (i >= 2) ms.Add(t); // the first runs warm up
        }
        ms.Sort();
        double p95 = ms[Math.Min(ms.Count - 1, (int)Math.Ceiling(ms.Count * 0.95) - 1)];
        double rtf = p95 / s.BlockMs;
        double latency = s.BlockMs + (p95 * 1.3 + 20) + s.AlgorithmicMs + 2;
        return new BenchmarkRun(q, loaded.Voice.Generator.Provider, s.BlockMs, ms.Average(), p95, rtf, latency, rtf <= RealTimeShare);
    }

    /// <summary>A vowel-like buzz with a moving pitch and pauses (what the models cost doesn't depend on the words).</summary>
    public static float[] SpeechLike(int rate, double seconds)
    {
        var x = new float[(int)(rate * seconds)];
        var rng = new Random(5);
        double phase = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double t = (double)i / rate;
            double syll = t % 0.6;
            double env = syll < 0.45 ? Math.Sin(Math.PI * syll / 0.45) : 0;
            double f0 = 120 + 40 * Math.Sin(2 * Math.PI * 0.7 * t);
            phase += f0 / rate;
            double v = 0;
            for (int h = 1; h <= 12; h++) v += Math.Sin(2 * Math.PI * h * phase) / h * (h is 3 or 4 or 8 ? 2 : 1);
            x[i] = (float)(0.15 * env * v + 0.003 * (rng.NextDouble() - 0.5));
        }
        return x;
    }
}
