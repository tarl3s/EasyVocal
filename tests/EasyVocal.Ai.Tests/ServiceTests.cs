using EasyVocal.Ai.Engine;
using EasyVocal.Ai.Rvc;
using EasyVocal.Ai.Runtime;
using EasyVocal.Ai.Store;
using EasyVocal.Ai.Tests.Helpers;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Tests.Helpers;
using Xunit.Abstractions;

namespace EasyVocal.Ai.Tests;

/// <summary>The whole engine (pack → models → streams → converter) on the tiny pack: fast, on every OS.</summary>
[Collection("Timing")]
public class ServiceTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string data = AiFixtures.TinyDataFolder();
    private readonly AiVoiceService service;

    public ServiceTests(ITestOutputHelper output)
    {
        this.output = output;
        service = new AiVoiceService(data) { Threads = 2 };
    }

    public void Dispose()
    {
        service.Dispose();
        TestFolders.Delete(data);
    }

    [Fact]
    public void A_pack_makes_the_service_available_and_lists_its_voices()
    {
        Assert.True(service.Available, service.UnavailableReason);
        Assert.Equal(new[] { AiFixtures.VoiceA, AiFixtures.VoiceB }, service.Voices.All().Select(v => v.Id).OrderBy(x => x));
        Assert.Null(service.Voices.Check(service.Voices.Find(AiFixtures.VoiceA)!));
        var empty = new AiVoiceService(TestFolders.New("EV_AiEmpty_"));
        try
        {
            Assert.False(empty.Available);
            Assert.Contains("isn't installed", empty.UnavailableReason);
        }
        finally
        {
            empty.Dispose();
            TestFolders.Delete(empty.Pack.Root[..^3]);
        }
    }

    [Theory]
    [InlineData(AiQuality.Light, "yin")]
    [InlineData(AiQuality.Balanced, "rmvpe")]
    [InlineData(AiQuality.Max, "rmvpe")]
    public async Task Every_level_streams_through_the_converter(AiQuality q, string pitch)
    {
        using var conv = await service.CreateConverterAsync(AiFixtures.VoiceA, q, AiDevice.Cpu, CancellationToken.None);
        var x = Speech48(2.5);
        var y = Lockstep(conv, x);
        var st = conv.Stats;
        output.WriteLine($"{q}: {st}");
        Assert.True(st.Ready);
        Assert.Equal(pitch, st.Pitch);
        Assert.Equal(0, st.Underruns);
        Assert.Null(st.Error);
        Assert.All(y, v => Assert.True(float.IsFinite(v)));
        var s = AiLevels.Settings(q, gpu: false);
        Assert.InRange(conv.LatencyMs, s.BlockMs + s.AlgorithmicMs, s.BlockMs + s.AlgorithmicMs + 500);
    }

    [Fact]
    public async Task Streams_share_the_loaded_models_and_unload_them_when_done()
    {
        var a = await service.CreateConverterAsync(AiFixtures.VoiceA, AiQuality.Light, AiDevice.Cpu, CancellationToken.None);
        var b = await service.CreateConverterAsync(AiFixtures.VoiceB, AiQuality.Light, AiDevice.Cpu, CancellationToken.None);
        var follower = (AiVoiceConverter)a.CreateFollower();
        Lockstep(a, Speech48(0.6));
        Lockstep(b, Speech48(0.6));
        Lockstep(follower, Speech48(0.6));
        string baseKey = "base:" + BaseModels.BaseGeneratorId + ":Cpu";
        Assert.Equal(3, service.Models.RefsOf(baseKey)); // one base generator for both voices and the follower
        a.Dispose(); b.Dispose(); follower.Dispose();
        await WaitFor(() => service.Models.RefsOf(baseKey) == 0);
        service.Models.Trim(all: true);
        Assert.False(service.Models.IsLoaded(baseKey));
    }

    [Fact]
    public void An_offline_conversion_keeps_the_length_and_follows_the_auto_pitch()
    {
        using var v = service.Load(AiFixtures.VoiceA, AiQuality.Max, AiDevice.Cpu);
        var x = Speech48(1.5);
        var y = RvcOffline.Convert(v.Voice, x);
        Assert.Equal(x.Length, y.Length);
        Assert.All(y, s => Assert.True(float.IsFinite(s)));
        Assert.Equal(220, v.Voice.TargetHz);
    }

    [Fact]
    public void Voice_cards_are_added_for_the_installed_voices_once()
    {
        var s = new AppSettings();
        s.Voices.AddRange(BuiltInVoices.All());
        Assert.True(service.SyncPresets(s));
        var a = s.Voices.Single(v => v.AiVoiceId == AiFixtures.VoiceA);
        Assert.Equal(VoiceKind.Ai, a.Kind);
        Assert.Equal("Tiny A", a.Name);
        Assert.False(service.SyncPresets(s));
    }

    [Fact]
    public async Task The_benchmark_measures_every_level_and_suggests_one()
    {
        var r = await AiBenchmark.RunAsync(service, AiFixtures.VoiceA, blocks: 4, tryGpu: !RealModels.OnCi);
        foreach (var run in r.Runs) output.WriteLine(run.ToString());
        output.WriteLine($"suggested {r.Suggested} on {r.Provider}");
        Assert.Equal(3, r.Runs.Count(x => x.Provider == "CPU"));
        Assert.True(r.AnyRealTime); // the tiny models are fast anywhere
        var settings = new AiSettings();
        r.SaveTo(settings);
        Assert.Equal(AiLevels.Id(r.Suggested), settings.SuggestedQuality);
        Assert.True(settings.MeasuredBlockMs.ContainsKey("light:CPU"));
        // the measured block time feeds the stream's cushion
        var svc = new AiVoiceService(data, () => settings);
        try { Assert.Equal(AiLevels.CushionFor(settings.MeasuredBlockMs["light:CPU"]), svc.CushionMs(AiQuality.Light, "CPU"), 3); }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void Quality_and_device_come_from_the_voice_then_the_settings()
    {
        var s = new AiSettings();
        var svc = new AiVoiceService(data, () => s);
        try
        {
            Assert.Equal(AiQuality.Balanced, svc.QualityFor(null));
            s.SuggestedQuality = "light";
            Assert.Equal(AiQuality.Light, svc.QualityFor(null));
            s.Quality = "max";
            Assert.Equal(AiQuality.Max, svc.QualityFor(null));
            Assert.Equal(AiQuality.Light, svc.QualityFor("light"));
            Assert.Equal(AiDevice.Cpu, svc.DeviceFor()); // until the benchmark found the GPU better
            s.SuggestedDevice = "gpu";
            Assert.Equal(AiDevice.Gpu, svc.DeviceFor());
            s.Device = "cpu";
            Assert.Equal(AiDevice.Cpu, svc.DeviceFor());
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task The_GPU_is_used_when_it_works_here_and_the_CPU_otherwise()
    {
        output.WriteLine("providers: " + string.Join(", ", OrtRuntime.Providers()) + "; this system's GPU provider: " + (OrtRuntime.GpuProvider() ?? "none"));
        using var conv = await service.CreateConverterAsync(AiFixtures.VoiceA, AiQuality.Light, AiDevice.Gpu, CancellationToken.None);
        Lockstep(conv, Speech48(1.2));
        var st = conv.Stats;
        output.WriteLine(st.ToString());
        Assert.True(st.Ready && st.Error == null, st.ToString());
        Assert.Contains(st.Provider, new[] { "CPU", "DirectML", "CoreML", "CUDA" });
        if (OrtRuntime.GpuProvider() == null) Assert.Equal("CPU", st.Provider);
    }

    internal static float[] Speech48(double seconds) => AiBenchmark.SpeechLike(48000, seconds);

    internal static float[] Lockstep(AiVoiceConverter c, float[] input)
    {
        const int chunk = 480;
        var o = new float[input.Length];
        var buf = new float[chunk];
        bool stuck = false;
        for (int i = 0; i + chunk <= input.Length; i += chunk)
        {
            long need = c.WrittenSamples + chunk - (c.BlockSamples + c.CushionSamples);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!stuck && c.ProducedSamples < need && c.WrittenSamples >= c.BlockSamples)
            {
                if (sw.ElapsedMilliseconds > 20000 || c.Stats.Error != null) stuck = true;
                Thread.Sleep(1);
            }
            input.AsSpan(i, chunk).CopyTo(buf);
            c.Process(buf);
            buf.CopyTo(o, i);
        }
        return o;
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(10);
        Assert.True(cond());
    }
}
