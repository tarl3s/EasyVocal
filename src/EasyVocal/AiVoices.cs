using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EasyVocal.Ai;
using EasyVocal.Ai.Runtime;
using EasyVocal.Ai.Store;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;

namespace EasyVocal;

/// <summary>
/// What the windows need of the AI voices beyond playing them: the AI pack (download, install, remove), the voices
/// (list, import by file or link, delete) and the benchmark. <see cref="AiManager"/> is the real one;
/// <see cref="StandInAi"/> draws every state in the smoke test without ONNX Runtime.
/// </summary>
public interface IAiVoices : IVoiceConverterSource
{
    /// <summary>The AI pack is installed (a file check).</summary>
    bool PackInstalled { get; }

    /// <summary>Looks at the release: what this computer would download.</summary>
    Task<PackOffer> FindPackAsync(CancellationToken ct);

    /// <summary>Bytes of <paramref name="offer"/> already downloaded (a stopped download goes on).</summary>
    long Downloaded(PackOffer offer);

    /// <summary>Downloads and installs the pack; true when EasyVocal must restart to finish (the runtime in use is replaced then).</summary>
    Task<bool> InstallPackAsync(PackOffer offer, IProgress<PackProgress>? progress, CancellationToken ct);

    /// <summary>Removes the pack (and the imported voices with <paramref name="alsoImported"/>); nothing may play from it.</summary>
    Task RemovePackAsync(bool alsoImported);

    /// <summary>About how much space the pack and the voices take on disk.</summary>
    long PackBytes { get; }

    IReadOnlyList<VoiceManifest> Voices();

    VoiceManifest? Find(string aiVoiceId);

    Task<VoiceManifest> ImportFilesAsync(IReadOnlyList<string> files, string? name, string? emoji, IProgress<ImportProgress>? progress, CancellationToken ct);

    Task<VoiceManifest> ImportUrlAsync(string link, string? name, string? emoji, IProgress<ImportProgress>? progress, CancellationToken ct);

    /// <summary>Deletes an imported voice's files (never a bundled one).</summary>
    bool DeleteVoice(string aiVoiceId);

    /// <summary>Measures this PC (each level) and suggests one.</summary>
    Task<BenchmarkResult> BenchmarkAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>A while after a voice or level change: the models nobody uses any more are unloaded.</summary>
    void TrimSoon();
}

/// <summary>The AI voices of a data folder (<see cref="AiVoiceService"/> + <see cref="PackInstaller"/> + <see cref="VoiceImporter"/>).</summary>
public sealed class AiManager : IAiVoices, IDisposable
{
    private readonly AiVoiceService service;
    private readonly PackInstaller installer;
    private readonly VoiceImporter importer;

    /// <param name="releaseApi">Where the pack's release is described (null = GitHub; the e2e run: a local server).</param>
    public AiManager(string dataFolder, Func<AiSettings> settings, string? releaseApi = null, string? assetName = null)
    {
        service = new AiVoiceService(dataFolder, settings);
        installer = new PackInstaller(service.Pack, releaseApi: releaseApi, assetName: assetName);
        importer = new VoiceImporter(service.Pack, service.Voices);
    }

    public AiVoiceService Service => service;

    public bool Available => service.Available;
    public string? UnavailableReason => service.UnavailableReason;
    public bool PackInstalled => service.Pack.Installed;

    public Task<IVoiceConverter> CreateAsync(string aiVoiceId, string? quality, bool cpuOnly, CancellationToken ct) =>
        service.CreateAsync(aiVoiceId, quality, cpuOnly, ct);

    public IVoiceConverter StandIn(string aiVoiceId) => service.StandIn(aiVoiceId);

    public bool SyncPresets(AppSettings settings) => service.SyncPresets(settings);

    public Task<PackOffer> FindPackAsync(CancellationToken ct) => installer.FindAsync(ct);

    public long Downloaded(PackOffer offer) => installer.Downloaded(offer);

    public async Task<bool> InstallPackAsync(PackOffer offer, IProgress<PackProgress>? progress, CancellationToken ct)
    {
        bool restart = await installer.InstallAsync(offer, progress, ct).ConfigureAwait(false);
        service.PackChanged();
        return restart;
    }

    public Task RemovePackAsync(bool alsoImported) => Task.Run(() =>
    {
        service.UnloadUnused();
        installer.Remove(alsoImported);
    });

    public long PackBytes
    {
        get
        {
            try
            {
                var root = new DirectoryInfo(service.Pack.Root);
                return root.Exists ? root.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }
    }

    public IReadOnlyList<VoiceManifest> Voices() => service.Voices.All();

    public VoiceManifest? Find(string aiVoiceId) => service.Voices.Find(aiVoiceId);

    public Task<VoiceManifest> ImportFilesAsync(IReadOnlyList<string> files, string? name, string? emoji, IProgress<ImportProgress>? progress, CancellationToken ct) =>
        importer.ImportFilesAsync(files, name, emoji, null, progress, ct);

    public Task<VoiceManifest> ImportUrlAsync(string link, string? name, string? emoji, IProgress<ImportProgress>? progress, CancellationToken ct) =>
        importer.ImportUrlAsync(link, name, emoji, progress, ct);

    public bool DeleteVoice(string aiVoiceId)
    {
        var m = service.Voices.Find(aiVoiceId);
        if (m == null || m.BuiltIn) return false;
        service.UnloadUnused();
        return service.Voices.Delete(aiVoiceId);
    }

    public Task<BenchmarkResult> BenchmarkAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var first = service.Voices.All().FirstOrDefault() ?? throw new InvalidOperationException("The AI pack has no voices.");
        return AiBenchmark.RunAsync(service, first.Id, progress, ct);
    }

    public void TrimSoon() => _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
    {
        try { service.UnloadUnused(); }
        catch (Exception ex) { AppLog.Error("Unloading unused AI models failed", ex); }
    }, TaskScheduler.Default);

    public void Dispose() => service.Dispose();
}

/// <summary>
/// The smoke test's AI voices: every state the windows show (no pack, downloading, installed, a voice loading or
/// failing, an import) without ONNX Runtime or a network. Its "voices" are a pitch shift.
/// </summary>
internal sealed class StandInAi : IAiVoices
{
    private readonly List<VoiceManifest> voices = new();
    private bool installed;

    public bool Available => installed;
    public string? UnavailableReason => installed ? null : "The AI pack isn't installed.";
    public bool PackInstalled => installed;

    /// <summary>The next load waits until released (to draw the loading spinner) or fails with this message.</summary>
    public TaskCompletionSource? HoldLoads { get; set; }
    public string? FailNextLoad { get; set; }
    public string? LastFailure { get; private set; }

    public async Task<IVoiceConverter> CreateAsync(string aiVoiceId, string? quality, bool cpuOnly, CancellationToken ct)
    {
        if (HoldLoads is { } hold) await hold.Task.WaitAsync(ct);
        if (FailNextLoad is string why)
        {
            FailNextLoad = null;
            LastFailure = why;
            throw new InvalidDataException(why);
        }
        return new ShiftConverter();
    }

    public bool SyncPresets(AppSettings s)
    {
        bool changed = false;
        foreach (var m in voices)
            if (!s.Voices.Any(v => v.Kind == VoiceKind.Ai && v.AiVoiceId == m.Id))
            {
                s.Voices.Add(new VoicePreset { Id = m.Id, Name = m.Name, Emoji = m.Emoji, BuiltIn = m.BuiltIn, Kind = VoiceKind.Ai, AiVoiceId = m.Id });
                changed = true;
            }
        return changed;
    }

    public Task<PackOffer> FindPackAsync(CancellationToken ct) =>
        Task.FromResult(new PackOffer("EasyVocal-AI-test.zip", new Uri("https://example.invalid/pack.zip"), 640L * 1024 * 1024, new string('0', 64)));

    public long Downloaded(PackOffer offer) => 0;

    /// <summary>While set, an install stops at this share of the download (to draw the progress).</summary>
    public TaskCompletionSource? HoldInstall { get; set; }

    public async Task<bool> InstallPackAsync(PackOffer offer, IProgress<PackProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new PackProgress(PackStage.Downloading, offer.Size * 42 / 100, offer.Size));
        if (HoldInstall is { } hold) await hold.Task.WaitAsync(ct);
        progress?.Report(new PackProgress(PackStage.Installing, 0, null));
        installed = true;
        foreach (var (id, name, emoji) in new[] { ("bright-woman", "Bright woman", "👩"), ("deep-man", "Deep man", "👨"), ("old-man", "Old man", "👴") })
            voices.Add(new VoiceManifest { Id = id, Name = name, Emoji = emoji, BuiltIn = true, Kind = VoiceKinds.Speaker, Licence = "CC BY 4.0", Attribution = "VCTK" });
        return false;
    }

    public Task RemovePackAsync(bool alsoImported)
    {
        installed = false;
        voices.RemoveAll(v => v.BuiltIn || alsoImported);
        return Task.CompletedTask;
    }

    public long PackBytes => installed ? 700L * 1024 * 1024 : 0;

    public IReadOnlyList<VoiceManifest> Voices() => installed ? voices.ToList() : Array.Empty<VoiceManifest>();

    public VoiceManifest? Find(string aiVoiceId) => voices.FirstOrDefault(v => v.Id == aiVoiceId);

    public string? FailNextImport { get; set; }

    public Task<VoiceManifest> ImportFilesAsync(IReadOnlyList<string> files, string? name, string? emoji, IProgress<ImportProgress>? progress, CancellationToken ct) =>
        Import(name ?? Path.GetFileNameWithoutExtension(files[0]), emoji, progress, ct);

    public Task<VoiceManifest> ImportUrlAsync(string link, string? name, string? emoji, IProgress<ImportProgress>? progress, CancellationToken ct) =>
        Import(name ?? "Linked voice", emoji, progress, ct);

    private async Task<VoiceManifest> Import(string name, string? emoji, IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ImportProgress(ImportStage.Downloading, 3 * 1048576, 8 * 1048576));
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (FailNextImport is string why)
        {
            FailNextImport = null;
            throw new NotSupportedException(why);
        }
        var m = new VoiceManifest { Id = "user-" + name.ToLowerInvariant().Replace(' ', '-'), Name = name, Emoji = emoji ?? "🧠", Kind = VoiceKinds.Pth, Source = "smoke test" };
        voices.Add(m);
        return m;
    }

    public bool DeleteVoice(string aiVoiceId) => voices.RemoveAll(v => v.Id == aiVoiceId && !v.BuiltIn) > 0;

    public Task<BenchmarkResult> BenchmarkAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("balanced on the processor…");
        var runs = new[]
        {
            new BenchmarkRun(AiQuality.Light, "CPU", 200, 60, 80, 0.4, 330, true),
            new BenchmarkRun(AiQuality.Balanced, "CPU", 200, 110, 140, 0.7, 390, true),
            new BenchmarkRun(AiQuality.Max, "CPU", 250, 260, 300, 1.2, 620, false),
        };
        return Task.FromResult(new BenchmarkResult(AiQuality.Balanced, AiDevice.Cpu, "CPU", true, runs));
    }

    public void TrimSoon() { }

    /// <summary>A pitch shift standing in for a converted voice (ready at once).</summary>
    private sealed class ShiftConverter : IVoiceConverter
    {
        private readonly VoiceShifter shifter = new(AudioFormat.SampleRate) { PitchSemitones = 5 };
        private volatile bool disposed;
        public int LatencySamples => AudioFormat.SampleRate * 380 / 1000; // what the readout shows
        public void Process(Span<float> mono) { if (disposed) mono.Clear(); else shifter.Process(mono); }
        public void Reset() => shifter.Reset();
        public void Dispose() => disposed = true;
    }
}
