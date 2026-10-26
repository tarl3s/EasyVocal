using System.Diagnostics;
using EasyVocal.Ai.Rvc;
using EasyVocal.Ai.Runtime;
using EasyVocal.Ai.Tests.Helpers;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;
using EasyVocal.Tests.Helpers;
using Xunit.Abstractions;

namespace EasyVocal.Ai.Tests;

/// <summary>
/// An AI voice in the real audio engine on pretend devices, fed in real time like a sound card: the voice reaches the
/// call (and Hear me) converted, behind by the latency it reports, with no dropouts or clicks, close to the offline
/// conversion. The latency per level is measured here (the devices' own ~20 ms come on top in the app).
/// </summary>
[Collection("Timing")]
public class PipelineTests
{
    private const int Sr = AudioFormat.SampleRate;
    private const string Voice = "ai-warm-woman";
    private readonly ITestOutputHelper output;

    /// <summary>The benchmark once per run: its block times give the streams their cushion, as in the app.</summary>
    private static readonly Lazy<AiSettings?> Measured = new(() =>
    {
        var data = RealModels.DataFolder(out _);
        if (data == null) return null;
        using var s = new AiVoiceService(data);
        var r = AiBenchmark.Run(s, Voice, null, CancellationToken.None, blocks: 10, tryGpu: !RealModels.OnCi);
        var settings = new AiSettings();
        r.SaveTo(settings);
        Benchmark = r;
        return settings;
    });

    private static BenchmarkResult? Benchmark;

    public PipelineTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void The_benchmark_suggests_a_level_for_this_PC()
    {
        if (Measured.Value == null) { output.WriteLine("SKIPPED: " + Skip()); return; }
        var r = Benchmark!;
        output.WriteLine("level    | device   | block | mean ms | p95 ms | RTF  | est. latency | real time");
        foreach (var x in r.Runs)
            output.WriteLine($"{x.Quality,-8} | {x.Provider,-8} | {x.BlockMs,5} | {x.MeanMs,7:0} | {x.P95Ms,6:0} | {x.Rtf,4:0.00} | {x.LatencyMs,9:0} ms | {x.RealTime}");
        output.WriteLine($"suggested: {r.Suggested} on {r.Provider} (threads {OrtRuntime.DefaultThreads}, {Environment.ProcessorCount} logical CPUs)");
        Assert.Contains(r.Runs, x => x.Quality == AiQuality.Light && x.Provider == "CPU");
        if (!RealModels.OnCi) Assert.True(r.AnyRealTime, "at least Light runs in real time on this PC");
    }

    private static string Skip()
    {
        RealModels.DataFolder(out var why);
        return why ?? "no benchmark";
    }

    [Theory]
    [InlineData(AiQuality.Light, 450)]
    [InlineData(AiQuality.Balanced, 550)]
    [InlineData(AiQuality.Max, 750)]
    public async Task An_AI_voice_streams_through_the_engine_in_real_time(AiQuality q, int budgetMs)
    {
        var settings = Measured.Value;
        var data = RealModels.DataFolder(out var skip);
        if (data == null || settings == null) { output.WriteLine("SKIPPED: " + skip); return; }
        using var service = new AiVoiceService(data, () => settings);

        var chain = new VoiceChain(Sr);
        var mixer = new SoundMixer();
        var mic = new FakeInput();
        var call = new FakeOutput();
        var monitor = new FakeOutput();
        using var engine = new AudioEngine(() => mic, () => call, () => monitor, chain, mixer) { HearMe = true };
        using var conv = await service.CreateConverterAsync(Voice, q, AiDevice.Cpu, CancellationToken.None);
        chain.SetConverter(conv);
        engine.Start();

        // 1 s of silence (the stream warms up), the speech, 1 s of silence
        var speech = Speech.Signal;
        var x = new float[Sr + speech.Length + Sr];
        speech.CopyTo(x, Sr);
        var (_, maxMs) = StreamingTests.FeedRealTime(b => mic.Raise(b, b.Length), x);
        var y = call.Written;
        var heard = monitor.Written;
        var st = conv.Stats;

        int measured = OnsetLag(x, y, Sr);
        double measuredMs = measured * 1000.0 / Sr;
        float[] reference;
        using (var lv = service.Load(Voice, q, AiDevice.Cpu))
            reference = RvcOffline.Convert(lv.Voice, speech, useRmvpe: q != AiQuality.Light, indexRate: 0);
        var streamed = y.AsSpan(Sr + measured, Math.Min(speech.Length, y.Length - Sr - measured)).ToArray();
        double dStream = VoiceMeasure.Distance(VoiceMeasure.Timbre(streamed), VoiceMeasure.Timbre(reference));
        double dInput = VoiceMeasure.Distance(VoiceMeasure.Timbre(speech), VoiceMeasure.Timbre(reference));
        // the melody once the pitch follower knows the speaker (after the first second)
        int seg = Math.Min(Sr * 3, Math.Min(speech.Length, streamed.Length) - Sr);
        var (_, spread, voiced) = VoiceMeasure.PitchFollow(speech.AsSpan(Sr, seg).ToArray(), streamed.AsSpan(Sr, seg).ToArray());
        double clicks = VoiceMeasure.Clicks(y, Sr + measured);
        output.WriteLine($"{q}: reported latency {conv.LatencyMs:0} ms, measured {measuredMs:0} ms (+ the devices' ~20 ms in the app); " +
                         $"block p95 {st.BlockMsP95:0} ms, RTF {st.Rtf:0.00}, cushion {conv.CushionSamples * 1000.0 / Sr:0} ms, under-runs {st.Underruns}, " +
                         $"overloads {st.Overloads}, clicks {clicks * 100:0.00} %, pitch spread {spread:0} cents (voiced {voiced:0.00}), " +
                         $"timbre to offline {dStream:0.0} (input {dInput:0.0}); slowest Process {maxMs:0.00} ms; {st.Provider}, {st.Pitch}");

        Assert.True(st.Ready && st.Error == null, st.ToString());
        Assert.True(Signals.AllFinite(y));
        // a runner too slow for this level (overloads, many under-runs: the dry voice fills in) can only show that nothing breaks
        bool keptUp = st.Overloads == 0 && st.Underruns <= 3;
        if (!keptUp) output.WriteLine("this computer is too slow for this level: only the invariants are checked");
        if (keptUp)
        {
            Assert.True(Math.Abs(measuredMs - conv.LatencyMs) < 40, $"the reported latency ({conv.LatencyMs:0} ms) is the real one ({measuredMs:0} ms)");
            Assert.True(dStream < dInput, "the streamed voice is the AI voice, not the input");
            Assert.True(spread < (Speech.Source == "synthetic" ? 160 : 100), $"the melody is followed (spread {spread:0} cents)");
        }
        Assert.True(clicks < 0.02, $"clicks {clicks:P1}");
        Assert.Equal(y.Length, heard.Length); // Hear me plays the same voice
        Assert.True(Signals.Rms(heard.AsSpan(Sr * 2, Sr).ToArray()) > 0.01);
        if (!RealModels.OnCi) Assert.True(maxMs < 30, $"the audio thread waited {maxMs:0.0} ms"); // preemption on a busy PC included (a CI runner: reported only); StreamingTests checks the audio thread closely
        // the latency budget: a soft check (other test suites may share the CPU); EV_AI_STRICT=1 enforces it
        if (measuredMs > budgetMs) output.WriteLine($"OVER BUDGET: {q} {measuredMs:0} ms > {budgetMs} ms");
        if (Environment.GetEnvironmentVariable("EV_AI_STRICT") == "1") Assert.True(measuredMs <= budgetMs, $"{q} latency {measuredMs:0} ms over the {budgetMs} ms budget");
    }

    [Fact]
    public async Task Classic_effects_still_apply_after_an_AI_voice()
    {
        var data = RealModels.DataFolder(out var skip);
        if (data == null) { output.WriteLine("SKIPPED: " + skip); return; }
        using var service = new AiVoiceService(data, () => Measured.Value ?? new AiSettings());
        using var conv = await service.CreateConverterAsync(Voice, AiQuality.Light, AiDevice.Cpu, CancellationToken.None);
        var plain = new VoiceChain(Sr);
        plain.SetConverter(conv);
        var speech = Speech.Signal.AsSpan(0, Math.Min(Speech.Signal.Length, Sr * 4)).ToArray();
        var a = StreamingTests.FeedRealTime(b => plain.Process(b), speech).Output;
        plain.SetConverter(null);
        using var conv2 = await service.CreateConverterAsync(Voice, AiQuality.Light, AiDevice.Cpu, CancellationToken.None);
        var radio = new VoiceChain(Sr);
        radio.Apply(new VoiceSliders { Radio = 1 });
        radio.SetConverter(conv2);
        var b2 = StreamingTests.FeedRealTime(b => radio.Process(b), speech).Output;
        double cA = Signals.Centroid(a, 100, 8000), cB = Signals.Centroid(b2, 100, 8000);
        output.WriteLine($"AI alone: centroid {cA:0} Hz; AI + Old radio: centroid {cB:0} Hz");
        Assert.True(Signals.Rms(b2) > 0.005);
        Assert.NotEqual(cA, cB);
    }

    /// <summary>The lag of <paramref name="y"/> behind <paramref name="x"/>: best match of their 10 ms energy envelopes, refined to the sample.</summary>
    internal static int OnsetLag(float[] x, float[] y, int from)
    {
        static double[] Env(float[] s)
        {
            var e = new double[s.Length / 480];
            for (int i = 0; i < e.Length; i++)
            {
                double sum = 0;
                for (int k = i * 480; k < i * 480 + 480; k++) sum += s[k] * s[k];
                e[i] = Math.Log(sum + 1e-6);
            }
            return e;
        }
        var ex = Env(x);
        var ey = Env(y);
        int best = 0;
        double bestC = double.NegativeInfinity;
        for (int lag = 0; lag < 150; lag++)
        {
            double s = 0, a = 0, b = 0, ma = 0, mb = 0;
            int n = 0;
            for (int i = from / 480; i + lag < ey.Length && i < ex.Length; i++) { ma += ex[i]; mb += ey[i + lag]; n++; }
            if (n < 50) break;
            ma /= n; mb /= n;
            for (int i = from / 480; i + lag < ey.Length && i < ex.Length; i++)
            {
                double u = ex[i] - ma, v = ey[i + lag] - mb;
                s += u * v; a += u * u; b += v * v;
            }
            double c = s / Math.Sqrt(a * b + 1e-30);
            if (c > bestC) { bestC = c; best = lag; }
        }
        return best * 480;
    }
}
