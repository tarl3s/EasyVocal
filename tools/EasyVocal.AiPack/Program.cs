using System.Diagnostics;
using EasyVocal.Ai;
using EasyVocal.Ai.Rvc;
using EasyVocal.Ai.Runtime;
using EasyVocal.Ai.Store;
using EasyVocal.Core.Sounds;

// EasyVocal's AI pack tool. Run from the repository: dotnet run --project tools/EasyVocal.AiPack -- <command> ...
//   fetch  --cache <dir>                          download RVC's public files (SHA-256 checked) into <dir>
//   build  --data <dir> [--cache <dir>] [--int8 <hubert_int8.onnx>]
//                                                 make <dir>/AI: engine models + the bundled voices
//   voices --data <dir>                           (re)write only the bundled voices from bundled-voices.json
//   list   --data <dir>                           the installed voices
//   bench  --data <dir> [--voice <id>]            the one-time benchmark (what it would suggest here)
//   render --data <dir> --voice <id> --in <audio> --out <wav> [--quality light|balanced|max] [--stream] [--cpu]
//                                                 a recording through a voice (offline, or streamed like the app)
//   pack   --data <dir> --out <dir> [--rid win-x64|osx-arm64|osx-x64|linux-x64|all] [--cache <dir>]
//                                                 the release assets: EasyVocal-AI-<os>.zip + .sha256 from <dir>/AI and
//                                                 that OS's ONNX Runtime (NuGet, SHA-256 pinned)
//   blends --data <dir> --recipe <json> --in <wav>[;<wav>...] --out <dir>
//                                                 candidate bundled voices (a recipe like bundled-voices.json) rendered
//                                                 offline on each recording as <id>__<recording>.wav, to judge them
//                                                 (tools/ai/export/judge_voices.py); writes them into <dir>/AI/voices
//   install --data <dir> [--asset <name>] [--keep <dir>]
//                                                 the real install from the GitHub release, as the app does it
var a = new Args(args);
try
{
    switch (a.Command)
    {
        case "fetch": await Fetch(a.Get("cache") ?? DefaultCache()); break;
        case "build": await Build(a); break;
        case "voices":
        {
            var pack = new AiPack(a.Need("data"));
            var list = PackBuilder.WriteBundledVoices(Path.Combine(pack.EngineFolder, BaseModels.BaseGeneratorId + ".pth"), pack.VoicesFolder);
            foreach (var m in list) Console.WriteLine($"{m.Id}: {m.Name} ({m.Files[0].Sha256[..12]}…)");
            break;
        }
        case "list":
        {
            var lib = new VoiceLibrary(new AiPack(a.Need("data")));
            foreach (var m in lib.All()) Console.WriteLine($"{m.Emoji} {m.Id,-28} {m.Name,-24} {m.Kind,-12} {(m.BuiltIn ? "built-in" : m.Source)}");
            break;
        }
        case "bench": Bench(a); break;
        case "render": await Render(a); break;
        case "pack":
        {
            var root = new AiPack(a.Need("data")).Root;
            var rid = a.Get("rid") ?? "all";
            var cache = Path.Combine(Path.GetDirectoryName(DefaultCache())!, "nuget");
            foreach (var r in rid == "all" ? PackRelease.Rids : new[] { rid })
            {
                var zip = await PackRelease.Make(root, r, a.Need("out"), a.Get("cache") ?? cache);
                Console.WriteLine($"{Path.GetFileName(zip)}: {new FileInfo(zip).Length / 1048576.0:0.0} MB, sha256 {File.ReadAllText(zip + ".sha256").Split(' ')[0]}");
            }
            break;
        }
        case "blends": Blends(a); break;
        case "install": await PackRelease.Install(a.Need("data"), a.Get("asset"), a.Get("keep")); break;
        default:
            Console.WriteLine("commands: fetch | build | voices | list | bench | render | pack | install  (see tools/EasyVocal.AiPack/Program.cs)");
            return 2;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("failed: " + ex.Message);
    return 1;
}

static string DefaultCache() => Path.Combine(Path.GetTempPath(), "EV_AiModelCache", "upstream");

static async Task<IReadOnlyDictionary<string, string>> Fetch(string cache)
{
    long last = 0;
    var p = new Progress<(BaseFile File, DownloadProgress Progress)>(x =>
    {
        if (x.Progress.Done - last < 20 * 1048576 && x.Progress.Done != x.Progress.Total) return;
        last = x.Progress.Done;
        Console.WriteLine($"  {x.File.Id}: {x.Progress.Done / 1048576} / {x.File.Size / 1048576} MB");
    });
    var files = await BaseModels.FetchAsync(cache, null, p);
    foreach (var (id, path) in files) Console.WriteLine($"{id} -> {path}");
    return files;
}

static async Task Build(Args a)
{
    var pack = new AiPack(a.Need("data"));
    var files = await Fetch(a.Get("cache") ?? DefaultCache());
    var sw = Stopwatch.StartNew();
    PackBuilder.Build(pack, files, a.Get("int8"), new Progress<string>(Console.WriteLine));
    Console.WriteLine($"AI pack in {pack.Root} ({sw.Elapsed.TotalSeconds:0} s)");
    foreach (var f in Directory.EnumerateFiles(pack.Root, "*", SearchOption.AllDirectories))
        Console.WriteLine($"  {Path.GetRelativePath(pack.Root, f),-50} {new FileInfo(f).Length / 1048576.0,8:0.0} MB");
}

static void Blends(Args a)
{
    var pack = new AiPack(a.Need("data"));
    var recipe = System.Text.Json.JsonSerializer.Deserialize<BundledRecipe>(File.ReadAllText(a.Need("recipe")), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
    PackBuilder.WriteBundledVoices(Path.Combine(pack.EngineFolder, BaseModels.BaseGeneratorId + ".pth"), pack.VoicesFolder, recipe);
    var inputs = a.Need("in").Split(';').Select(p => (Name: Path.GetFileNameWithoutExtension(p), Audio: SoundLoader.Load(p))).ToList();
    var outDir = a.Need("out");
    Directory.CreateDirectory(outDir);
    using var s = new AiVoiceService(a.Need("data"));
    var sw = Stopwatch.StartNew();
    foreach (var e in recipe.Voices)
    {
        using var v = s.Load(e.Id, AiQuality.Balanced, AiDevice.Cpu);
        foreach (var (name, x) in inputs)
            SoundWriter.WriteWav(Path.Combine(outDir, e.Id + "__" + name + ".wav"), RvcOffline.Convert(v.Voice, x, indexRate: 0));
        Console.WriteLine($"{e.Id}: {string.Join(" + ", e.Mix.Select(m => $"{m[1]:0.##}x{(int)m[0]}"))} ({sw.Elapsed.TotalSeconds:0} s)");
    }
}

static void Bench(Args a)
{
    using var s = new AiVoiceService(a.Need("data"));
    var voice = a.Get("voice") ?? s.Voices.All().First().Id;
    var r = AiBenchmark.Run(s, voice, new Progress<string>(Console.WriteLine), CancellationToken.None);
    Console.WriteLine("level    | device   | block | mean ms | p95 ms | RTF  | latency | real time");
    foreach (var x in r.Runs)
        Console.WriteLine($"{x.Quality,-8} | {x.Provider,-8} | {x.BlockMs,5} | {x.MeanMs,7:0} | {x.P95Ms,6:0} | {x.Rtf,4:0.00} | {x.LatencyMs,4:0} ms | {x.RealTime}");
    Console.WriteLine($"suggested: {r.Suggested} on {r.Provider}{(r.AnyRealTime ? "" : " (nothing ran in real time: expect gaps)")}");
}

static async Task Render(Args a)
{
    using var s = new AiVoiceService(a.Need("data"));
    var q = AiLevels.Parse(a.Get("quality")) ?? AiQuality.Balanced;
    var device = a.Has("cpu") ? AiDevice.Cpu : AiDevice.Auto;
    var x = SoundLoader.Load(a.Need("in"));
    float[] y;
    if (!a.Has("stream"))
    {
        using var v = s.Load(a.Need("voice"), q, device);
        y = RvcOffline.Convert(v.Voice, x, useRmvpe: q != AiQuality.Light);
    }
    else
    {
        using var c = await s.CreateConverterAsync(a.Need("voice"), q, device, CancellationToken.None);
        var buf = new float[480];
        var all = new List<float>();
        var clock = Stopwatch.StartNew();
        var padded = x.Concat(new float[48000]).ToArray();
        for (int i = 0; i + 480 <= padded.Length; i += 480)
        {
            while (clock.Elapsed.TotalMilliseconds < i / 48.0) Thread.Sleep(1);
            padded.AsSpan(i, 480).CopyTo(buf);
            c.Process(buf);
            all.AddRange(buf);
        }
        y = all.Skip(c.LatencySamples).Take(x.Length).ToArray();
        Console.WriteLine(c.Stats);
    }
    SoundWriter.WriteWav(a.Need("out"), y);
    Console.WriteLine("wrote " + a.Need("out"));
}

internal sealed class Args
{
    private readonly string[] raw;
    public Args(string[] raw) => this.raw = raw;
    public string Command => raw.Length > 0 ? raw[0] : "";
    public string? Get(string key)
    {
        for (int i = 1; i < raw.Length - 1; i++) if (raw[i] == "--" + key) return raw[i + 1];
        return null;
    }
    public bool Has(string key) => raw.Contains("--" + key);
    public string Need(string key) => Get(key) ?? throw new ArgumentException("--" + key + " is needed");
}
