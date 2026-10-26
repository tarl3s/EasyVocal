using EasyVocal.Ai.Engine;
using EasyVocal.Ai.Import;
using EasyVocal.Ai.Rvc;
using EasyVocal.Ai.Runtime;
using EasyVocal.Ai.Store;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Logging;
using EasyVocal.Core.Model;

namespace EasyVocal.Ai;

/// <summary>A voice loaded for one quality and device, with the lease that keeps its models loaded.</summary>
public sealed class LoadedVoice : IDisposable
{
    internal LoadedVoice(VoiceManifest manifest, RvcVoice voice, AiQuality quality, IReadOnlyList<string> keys, IDisposable lease)
    {
        Manifest = manifest;
        Voice = voice;
        Quality = quality;
        Keys = keys;
        this.lease = lease;
    }

    private readonly IDisposable lease;
    public VoiceManifest Manifest { get; }
    public RvcVoice Voice { get; }
    public AiQuality Quality { get; }
    internal IReadOnlyList<string> Keys { get; }

    public StreamSettings Settings => AiLevels.Settings(Quality, Voice.Generator.OnGpu, Voice.Generator.IsSingleGraph);

    public void Dispose() => lease.Dispose();
}

/// <summary>
/// The AI voices of one data folder: the pack (<see cref="AiPack"/>), its voices (<see cref="VoiceLibrary"/>), the
/// loaded models (<see cref="ModelCache"/>) and the converters the voice chain plays. What the app talks to.
/// </summary>
public sealed class AiVoiceService : IVoiceConverterSource, IDisposable
{
    private readonly Func<AiSettings> settings;
    private readonly Timer trimTimer;
    private bool disposed;

    /// <param name="settings">The person's AI settings (read each time a voice is loaded); null = defaults.</param>
    public AiVoiceService(string dataFolder, Func<AiSettings>? settings = null)
    {
        Pack = new AiPack(dataFolder);
        Voices = new VoiceLibrary(Pack);
        Models = new ModelCache();
        this.settings = settings ?? (() => new AiSettings());
        if (Directory.Exists(Pack.RuntimeFolder)) OrtRuntime.UseNativeFolder(Pack.RuntimeFolder);
        trimTimer = new Timer(_ => { try { Models.Trim(); } catch (Exception) { } }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public AiPack Pack { get; }
    public VoiceLibrary Voices { get; }
    public ModelCache Models { get; }

    /// <summary>Intra-op threads per model (default: physical cores, at most 6).</summary>
    public int Threads { get; set; } = OrtRuntime.DefaultThreads;

    public bool Available => UnavailableReason == null;

    public string? UnavailableReason
    {
        get
        {
            if (!Pack.Installed) return "The AI pack isn't installed.";
            return OrtRuntime.TryLoad(out var error) ? null : error;
        }
    }

    /// <summary>The quality to use: the voice's own, else the settings' ("auto" = the benchmark's suggestion, else Balanced).</summary>
    public AiQuality QualityFor(string? voiceQuality)
    {
        var s = settings();
        return AiLevels.Parse(voiceQuality) ?? AiLevels.Parse(s.Quality) ?? AiLevels.Parse(s.SuggestedQuality) ?? AiQuality.Balanced;
    }

    public AiDevice DeviceFor()
    {
        var s = settings();
        return s.Device?.ToLowerInvariant() switch
        {
            "cpu" => AiDevice.Cpu,
            "gpu" => AiDevice.Gpu,
            _ => s.SuggestedDevice == "gpu" ? AiDevice.Gpu : AiDevice.Cpu, // the processor until the benchmark found the GPU better
        };
    }

    async Task<IVoiceConverter> IVoiceConverterSource.CreateAsync(string aiVoiceId, string? quality, CancellationToken ct) =>
        await CreateConverterAsync(aiVoiceId, QualityFor(quality), DeviceFor(), ct).ConfigureAwait(false);

    /// <summary>Loads the voice (off the caller's thread) and makes a converter for the voice chain.</summary>
    public Task<AiVoiceConverter> CreateConverterAsync(string voiceId, AiQuality quality, AiDevice device, CancellationToken ct) =>
        Task.Run(() =>
        {
            var loaded = Load(voiceId, quality, device, ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                return NewConverter(loaded);
            }
            catch
            {
                loaded.Dispose();
                throw;
            }
        }, ct);

    /// <summary>A converter over a loaded voice; it owns <paramref name="loaded"/> (its first stream releases the lease).</summary>
    public AiVoiceConverter NewConverter(LoadedVoice loaded)
    {
        var s = loaded.Settings;
        var voice = loaded.Voice;
        string provider = voice.Generator.Provider;
        double cushion = CushionMs(loaded.Quality, provider);
        int first = 0;
        (Engine.IBlockConverter, IDisposable?) Make()
        {
            IDisposable lease = Interlocked.Exchange(ref first, 1) == 0
                ? loaded
                : Models.TryAddRefs(loaded.Keys) ? Models.Lease(loaded.Keys) : throw new InvalidOperationException("The voice was unloaded.");
            return (new RvcStream(voice, s), lease);
        }
        return new AiVoiceConverter(Make, s.BlockMs * 48, cushion, s.AlgorithmicMs, provider);
    }

    /// <summary>The cushion for a level: from the block time measured here (95th percentile), else the level's default.</summary>
    public double CushionMs(AiQuality q, string provider)
    {
        var m = settings().MeasuredBlockMs;
        double block = m != null && m.TryGetValue(AiLevels.Id(q) + ":" + provider, out var v) && v > 0 ? v : AiLevels.DefaultBlockMs(q);
        return AiLevels.CushionFor(block);
    }

    /// <summary>Loads a voice's models for a quality and device (cached); the caller disposes the result.</summary>
    public LoadedVoice Load(string voiceId, AiQuality quality, AiDevice device, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (UnavailableReason is string why) throw new InvalidOperationException(why);
        var m = Voices.Find(voiceId) ?? throw new FileNotFoundException("This AI voice isn't installed: " + voiceId);
        var keys = new List<string>();
        try
        {
            string dev = device.ToString();
            int threads = Threads;
            var entry = Generator(m, device, threads, keys);
            var gen = entry.Generator;
            ct.ThrowIfCancellationRequested();
            bool gpu = gen.OnGpu;
            bool int8 = AiLevels.PrefersInt8Content(quality, gpu);
            var contentPath = Pack.ContentModel(int8) ?? throw new FileNotFoundException("The AI pack has no content encoder.");
            var content = Models.Acquire("content:" + Path.GetFileName(contentPath) + ":" + dev,
                () => OrtRuntime.CreateFromFile("content encoder", contentPath, device, threads), keys);
            ct.ThrowIfCancellationRequested();
            var settingsForLevel = AiLevels.Settings(quality, gpu, gen.IsSingleGraph);
            OnnxModel? rmvpe = null;
            if (settingsForLevel.UseRmvpe && Pack.RmvpeModel is string rp)
                rmvpe = Models.Acquire("rmvpe:" + dev, () => OrtRuntime.CreateFromFile("pitch model", rp, device, threads), keys);
            FeatureIndex? index = null;
            if (settingsForLevel.IndexRate > 0 && m.Index is VoiceFile ixf)
                index = Models.Acquire("index:" + m.Id, () => FeatureIndex.Load(Voices.PathOf(m, ixf)), keys);
            float[]? speaker = Speaker(m, entry);
            var voice = new RvcVoice(content, gen, speaker, rmvpe, index) { TargetHz = m.TargetHz, Key = m.Key };
            return new LoadedVoice(m, voice, quality, keys, Models.Lease(keys));
        }
        catch
        {
            foreach (var k in keys) Models.Release(k);
            throw;
        }
    }

    private GeneratorEntry Generator(VoiceManifest m, AiDevice device, int threads, List<string> keys)
    {
        string dev = device.ToString();
        switch (m.Kind)
        {
            case VoiceKinds.Speaker:
            {
                string id = m.Base ?? BaseModels.BaseGeneratorId;
                var path = Pack.BaseGenerator(id) ?? throw new FileNotFoundException("The AI pack has no base model " + id + ".");
                return Models.Acquire("base:" + id + ":" + dev, () => LoadPth(path, device, threads, "base model"), keys);
            }
            case VoiceKinds.Pth:
            {
                var f = m.File("model") ?? throw new InvalidDataException("The voice has no model file.");
                var path = Voices.PathOf(m, f);
                return Models.Acquire("voice:" + m.Id + ":" + dev, () => LoadPth(path, device, threads, m.Name), keys);
            }
            case VoiceKinds.OnnxPair:
            {
                var e = m.File("enc") ?? throw new InvalidDataException("The voice has no enc.onnx.");
                var d = m.File("dec") ?? throw new InvalidDataException("The voice has no dec.onnx.");
                return Models.Acquire("voice:" + m.Id + ":" + dev, () => new GeneratorEntry(new RvcGenerator(m.SampleRate,
                    OrtRuntime.CreateFromFile(m.Name + " (encoder)", Voices.PathOf(m, e), device, threads),
                    OrtRuntime.CreateFromFile(m.Name + " (decoder)", Voices.PathOf(m, d), device, threads)), null), keys);
            }
            case VoiceKinds.OnnxSingle:
            {
                var f = m.File("model") ?? throw new InvalidDataException("The voice has no model file.");
                return Models.Acquire("voice:" + m.Id + ":" + dev, () => new GeneratorEntry(new RvcGenerator(m.SampleRate,
                    OrtRuntime.CreateFromFile(m.Name, Voices.PathOf(m, f), device, threads)), null), keys);
            }
            default:
                throw new NotSupportedException("Unknown kind of AI voice: " + m.Kind);
        }
    }

    /// <summary>A generator plus the speakers it was trained with (a .pth's emb_g).</summary>
    internal sealed record GeneratorEntry(RvcGenerator Generator, Weight? Speakers) : IDisposable
    {
        public void Dispose() => Generator.Dispose();
    }

    private GeneratorEntry LoadPth(string path, AiDevice device, int threads, string what)
    {
        var ck = TorchCheckpoint.Load(path);
        var built = Pack.BuildPth(ck);
        var enc = OrtRuntime.Create(what + " (encoder)", built.Enc, device, threads);
        try
        {
            var dec = OrtRuntime.Create(what + " (decoder)", built.Dec, device, threads);
            return new GeneratorEntry(new RvcGenerator(built.SampleRate, enc, dec), built.Speakers);
        }
        catch
        {
            enc.Dispose();
            throw;
        }
    }

    private float[]? Speaker(VoiceManifest m, GeneratorEntry entry)
    {
        if (entry.Generator.IsSingleGraph) return null;
        if (m.File("speaker") is VoiceFile sf) return ReadSpeaker(Voices.PathOf(m, sf));
        if (entry.Speakers != null) return RvcTemplates.SpeakerVector(entry.Speakers, 0); // a .pth voice: its own (first) speaker
        throw new InvalidDataException("The voice has no speaker.");
    }

    /// <summary>A speaker vector file: 256 little-endian floats.</summary>
    public static float[] ReadSpeaker(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length != RvcGenerator.Gin * 4) throw new InvalidDataException("A speaker file has the wrong size.");
        var v = new float[RvcGenerator.Gin];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }

    public static void WriteSpeaker(string path, float[] v)
    {
        if (v.Length != RvcGenerator.Gin) throw new ArgumentException("A speaker vector has 256 values.");
        var b = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, b, 0, b.Length);
        File.WriteAllBytes(path, b);
    }

    /// <summary>
    /// Voice cards for the installed voices: adds a <see cref="VoiceKind.Ai"/> preset for each voice that has none
    /// (id = the voice's id), names kept in sync for built-in ones. Returns true when something changed. Presets of
    /// voices that are gone are left alone (the pack may come back).
    /// </summary>
    public bool SyncPresets(AppSettings s)
    {
        bool changed = false;
        foreach (var m in Voices.All())
        {
            var p = s.Voices.FirstOrDefault(v => v.Kind == VoiceKind.Ai && v.AiVoiceId == m.Id);
            if (p == null)
            {
                if (s.Voices.Any(v => v.Id == m.Id)) continue;
                s.Voices.Add(new VoicePreset { Id = m.Id, Name = m.Name, Emoji = m.Emoji, BuiltIn = m.BuiltIn, Kind = VoiceKind.Ai, AiVoiceId = m.Id });
                changed = true;
            }
        }
        return changed;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        trimTimer.Dispose();
        Models.Dispose();
        AppLog.Info("AI: service closed");
    }
}
