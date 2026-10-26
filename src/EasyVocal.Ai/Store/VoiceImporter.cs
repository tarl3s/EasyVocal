using System.IO.Compression;
using EasyVocal.Ai.Import;
using EasyVocal.Ai.Rvc;
using EasyVocal.Ai.Runtime;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EasyVocal.Ai.Store;

/// <summary>What an import step is doing (for a progress line).</summary>
public enum ImportStage { Downloading, Unpacking, Checking, Copying }

public readonly record struct ImportProgress(ImportStage Stage, long Done, long? Total);

/// <summary>
/// Adds a voice the person brings: an RVC <c>.pth</c> (read without PyTorch), an <c>.onnx</c> (RVC WebUI's or
/// w-okada's single graph, or EasyVocal's enc + dec pair), optionally its <c>.index</c>, a <c>.zip</c> holding them,
/// or a link to any of those. Every model is checked before it is kept (built and run once on silence); a voice
/// is either added whole or not at all.
/// </summary>
public sealed class VoiceImporter
{
    public const long MaxDownloadBytes = 1536L * 1024 * 1024;
    public const long MaxModelBytes = 600L * 1024 * 1024;
    public const long MaxIndexBytes = 1536L * 1024 * 1024;

    private readonly AiPack pack;
    private readonly VoiceLibrary library;

    public VoiceImporter(AiPack pack, VoiceLibrary library)
    {
        this.pack = pack;
        this.library = library;
    }

    /// <param name="files">A .pth, an .onnx (or two: enc + dec), a .zip, plus optionally an .index.</param>
    /// <param name="name">The card's name; null = from the file name.</param>
    public Task<VoiceManifest> ImportFilesAsync(IReadOnlyList<string> files, string? name = null, string? emoji = null,
        string? source = null, IProgress<ImportProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => ImportFiles(files, name, emoji, source, progress, ct), ct);

    /// <summary>Downloads a link (a file or a .zip; Hugging Face page links work) and imports what it holds.</summary>
    public async Task<VoiceManifest> ImportUrlAsync(string link, string? name = null, string? emoji = null,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default, HttpClient? client = null)
    {
        var url = Downloader.Normalize(link) ?? throw new ArgumentException("That isn't a link EasyVocal can download from (https only).");
        string fileName = Downloader.FileNameOf(url) ?? "download";
        var work = WorkFolder();
        try
        {
            var path = Path.Combine(work, "download.bin");
            var p = progress == null ? null : new InlineProgress<DownloadProgress>(d => progress.Report(new ImportProgress(ImportStage.Downloading, d.Done, d.Total)));
            var info = new DownloadInfo();
            await Downloader.DownloadAsync(url, path, MaxDownloadBytes, null, p, ct, client, info).ConfigureAwait(false);
            // The name it is known by: the server's, else the link's; a name without a voice extension is told by its bytes.
            string name0 = info.ServerFileName ?? fileName;
            if (!Known(name0) && Downloader.Sniff(path) is string ext) name0 = Path.GetFileNameWithoutExtension(name0) + ext;
            var named = Path.Combine(work, Core.Storage.FileNames.IsPlain(name0) ? name0 : "download" + Path.GetExtension(name0));
            File.Move(path, named);
            path = named;
            return await Task.Run(() => ImportFiles(new[] { path }, name, emoji, url.ToString(), progress, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            Core.Storage.Cleanup.TryDelete(work);
        }
    }

    private static bool Known(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() is ".pth" or ".onnx" or ".index" or ".zip";

    private string WorkFolder()
    {
        var dir = Path.Combine(pack.Root, "tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private VoiceManifest ImportFiles(IReadOnlyList<string> files, string? name, string? emoji, string? source,
        IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        if (files.Count == 0) throw new ArgumentException("No file.");
        string? work = null;
        try
        {
            var list = new List<string>();
            foreach (var f in files)
            {
                if (Path.GetExtension(f).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    work ??= WorkFolder();
                    progress?.Report(new ImportProgress(ImportStage.Unpacking, 0, null));
                    list.AddRange(Unzip(f, work, ct));
                }
                else list.Add(f);
            }
            string? Pick(string ext, Func<string, long>? rank = null) =>
                list.Where(p => Path.GetExtension(p).Equals(ext, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => rank?.Invoke(p) ?? 0).FirstOrDefault();
            var pth = Pick(".pth", p => new FileInfo(p).Length);
            var onnx = list.Where(p => Path.GetExtension(p).Equals(".onnx", StringComparison.OrdinalIgnoreCase)).ToList();
            // RVC writes "trained_*.index" (no vectors) and "added_*.index" (the one it uses)
            var index = Pick(".index", p => (Path.GetFileName(p).StartsWith("added", StringComparison.OrdinalIgnoreCase) ? 1L << 40 : 0) + new FileInfo(p).Length);
            if (pth == null && onnx.Count == 0) throw new InvalidDataException("No voice model (.pth or .onnx) in what was given.");
            string baseName = Path.GetFileNameWithoutExtension(pth ?? onnx[0]);
            name = string.IsNullOrWhiteSpace(name) ? Prettify(baseName) : name.Trim();

            ct.ThrowIfCancellationRequested();
            progress?.Report(new ImportProgress(ImportStage.Checking, 0, null));
            var m = new VoiceManifest
            {
                Name = name,
                Emoji = string.IsNullOrWhiteSpace(emoji) ? "🧠" : emoji,
                Source = source ?? ("file: " + Path.GetFileName(pth ?? onnx[0])),
                AddedUtc = DateTime.UtcNow,
            };
            var copies = new List<(string From, string Role, string To)>();
            if (pth != null)
            {
                CheckSize(pth, MaxModelBytes);
                var ck = TorchCheckpoint.Load(pth, ct);
                var built = pack.BuildPth(ck);
                TryRun(built.Enc, built.Dec, built.SampleRate);
                m.Kind = VoiceKinds.Pth;
                m.SampleRate = built.SampleRate;
                m.Version = "v2";
                if (ck.Info is { Length: > 0 } info && info.Length < 300) m.Description = info;
                copies.Add((pth, "model", "model.pth"));
            }
            else
            {
                foreach (var o in onnx) CheckSize(o, MaxModelBytes);
                var kinds = onnx.Select(o =>
                {
                    var bytes = File.ReadAllBytes(o);
                    if (OnnxRewriter.HasExternalData(bytes)) throw new NotSupportedException("This ONNX voice keeps its weights in separate files, which EasyVocal doesn't open. Use the voice's .pth file instead.");
                    return (Path: o, Inputs: OnnxRewriter.InputNames(bytes));
                }).ToList();
                var enc = kinds.FirstOrDefault(k => k.Inputs.Contains("phone") && k.Inputs.Contains("lengths") && !k.Inputs.Contains("ds"));
                var dec = kinds.FirstOrDefault(k => k.Inputs.Contains("z_p") && k.Inputs.Contains("nsff0"));
                if (enc.Path != null && dec.Path != null)
                {
                    int sr = ProbeRate(dec.Path);
                    RunOnce(enc.Path); // the encoder must run too
                    m.Kind = VoiceKinds.OnnxPair;
                    m.SampleRate = sr;
                    copies.Add((enc.Path, "enc", "enc.onnx"));
                    copies.Add((dec.Path, "dec", "dec.onnx"));
                    var spk = list.FirstOrDefault(p => Path.GetFileName(p).Equals("speaker.f32", StringComparison.OrdinalIgnoreCase))
                              ?? throw new InvalidDataException("A two-part ONNX voice needs its speaker.f32.");
                    AiVoiceService.ReadSpeaker(spk);
                    copies.Add((spk, "speaker", "speaker.f32"));
                }
                else
                {
                    var single = kinds.FirstOrDefault(k => k.Inputs.Contains("pitch") && (k.Inputs.Contains("pitchf") || k.Inputs.Contains("nsff0")));
                    if (single.Path == null)
                    {
                        if (kinds.Any(k => !k.Inputs.Contains("pitch")))
                            throw new NotSupportedException("This ONNX voice was made without pitch. Only RVC voices with pitch are supported for now.");
                        throw new NotSupportedException("This ONNX file isn't an RVC voice EasyVocal knows (inputs: " + string.Join(", ", kinds[0].Inputs) + ").");
                    }
                    m.Kind = VoiceKinds.OnnxSingle;
                    m.SampleRate = ProbeRate(single.Path);
                    copies.Add((single.Path, "model", "model.onnx"));
                }
            }
            if (index != null)
            {
                CheckSize(index, MaxIndexBytes);
                var ix = FeatureIndex.Load(index);
                if (ix.D != ContentEncoder.Dim) throw new NotSupportedException("This .index is for another model type (not RVC v2).");
                if (ix.NTotal > 0) copies.Add((index, "index", "model.index"));
            }

            ct.ThrowIfCancellationRequested();
            m.Id = library.NewId(name);
            var dir = pack.VoiceFolder(m.Id);
            var stage = dir + ".partial";
            Core.Storage.Cleanup.TryDelete(stage);
            Directory.CreateDirectory(stage);
            try
            {
                progress?.Report(new ImportProgress(ImportStage.Copying, 0, null));
                foreach (var (from, role, to) in copies)
                {
                    ct.ThrowIfCancellationRequested();
                    var dst = Path.Combine(stage, to);
                    File.Copy(from, dst);
                    m.Files.Add(new VoiceFile { Role = role, Name = to, Size = new FileInfo(dst).Length, Sha256 = Downloader.Sha256Of(dst) });
                }
                m.Write(Path.Combine(stage, VoiceManifest.FileName));
                Directory.Move(stage, dir);
            }
            catch
            {
                Core.Storage.Cleanup.TryDelete(stage);
                throw;
            }
            return m;
        }
        finally
        {
            if (work != null) Core.Storage.Cleanup.TryDelete(work);
        }
    }

    private static void CheckSize(string path, long max)
    {
        var len = new FileInfo(path).Length;
        if (len > max) throw new InvalidDataException($"{Path.GetFileName(path)} is too big ({len / 1048576} MB).");
    }

    /// <summary>Most entries a voice's zip may list (a voice has a handful of files).</summary>
    public const int MaxZipEntries = 2000;

    /// <summary>
    /// The model files inside a zip, written under our own names (the zip's paths are never used): only the ones an
    /// import would pick (the biggest .pth, the .onnx files, the best .index, speaker.f32), each and all together
    /// within the size limits, checked while unpacking too (a zip may lie about its sizes).
    /// </summary>
    private static List<string> Unzip(string zipPath, string work, CancellationToken ct)
    {
        var o = new List<string>();
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxZipEntries) throw new InvalidDataException("This zip has too many files to be a voice.");
        static string Ext(ZipArchiveEntry e) => Path.GetExtension(e.Name).ToLowerInvariant();
        static bool Speaker(ZipArchiveEntry e) => e.Name.Equals("speaker.f32", StringComparison.OrdinalIgnoreCase);
        var models = zip.Entries.Where(e => e.Name.Length > 0 && !e.Name.StartsWith("D_", StringComparison.Ordinal) && !e.Name.StartsWith("G_", StringComparison.Ordinal)).ToList(); // G_/D_: training checkpoints
        var picked = new List<ZipArchiveEntry>();
        if (models.Where(e => Ext(e) == ".pth").MaxBy(e => e.Length) is { } pth) picked.Add(pth);
        picked.AddRange(models.Where(e => Ext(e) == ".onnx").Take(4));
        if (models.Where(e => Ext(e) == ".index").MaxBy(e => (e.Name.StartsWith("added", StringComparison.OrdinalIgnoreCase) ? 1L << 40 : 0) + e.Length) is { } index) picked.Add(index);
        if (models.FirstOrDefault(Speaker) is { } spk) picked.Add(spk);
        long total = 0;
        foreach (var e in picked)
        {
            long max = Ext(e) == ".index" ? MaxIndexBytes : MaxModelBytes;
            if (e.Length > max) throw new InvalidDataException($"{e.Name} is too big ({e.Length / 1048576} MB).");
            total += e.Length;
        }
        if (total > MaxDownloadBytes) throw new InvalidDataException("What is in this zip is too big.");
        long written = 0;
        int n = 0;
        foreach (var e in picked)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Ext(e);
            long max = ext == ".index" ? MaxIndexBytes : MaxModelBytes;
            var safe = Speaker(e) ? "speaker.f32" : $"{n++}_{Sanitize(Path.GetFileNameWithoutExtension(e.Name))}{ext}";
            var dst = Path.Combine(work, safe);
            using (var src = e.Open())
            using (var file = File.Create(dst))
            {
                var buf = new byte[1 << 16];
                long got = 0;
                int r;
                while ((r = src.Read(buf, 0, buf.Length)) > 0)
                {
                    got += r;
                    written += r;
                    if (got > max || got > e.Length || written > MaxDownloadBytes) throw new InvalidDataException($"{e.Name} is too big.");
                    file.Write(buf, 0, r);
                }
            }
            o.Add(dst);
        }
        return o;
    }

    private static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var r = new string(chars);
        return r.Length > 60 ? r[..60] : r.Length == 0 ? "file" : r;
    }

    private static string Prettify(string fileName)
    {
        var s = fileName.Replace('_', ' ').Replace('-', ' ').Trim();
        if (s.Length == 0) return "My AI voice";
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>Builds a .pth's generator on the CPU and runs it once on a few frames of silence.</summary>
    private static void TryRun(byte[] encBytes, byte[] decBytes, int sr)
    {
        using var enc = OrtRuntime.Create("import check (encoder)", encBytes, AiDevice.Cpu, 2);
        using var dec = OrtRuntime.Create("import check (decoder)", decBytes, AiDevice.Cpu, 2);
        using var g = new RvcGenerator(sr, enc, dec);
        using var run = g.NewRun(30, 10);
        Array.Fill(run.Coarse, 1L);
        run.Generate();
        if (run.Audio.Any(v => !float.IsFinite(v))) throw new InvalidDataException("The voice's model gives broken audio.");
    }

    /// <summary>The output rate of an ONNX generator: run it once on 10 frames and divide the samples by 10 ms.</summary>
    private static int ProbeRate(string path) => Rate(RunOnce(path) / ProbeFrames);

    private const int ProbeFrames = 10;

    /// <summary>
    /// Runs a voice's ONNX graph once on <see cref="ProbeFrames"/> frames of silence (inputs filled by their RVC names)
    /// and checks it gives finite numbers; returns how many output values came out. Only float32 / int64 inputs.
    /// </summary>
    private static int RunOnce(string path)
    {
        using var model = OrtRuntime.CreateFromFile("import check", path, AiDevice.Cpu, 2);
        foreach (var (name, meta) in model.InputMetadata)
            if (meta.ElementDataType is not (TensorElementType.Float or TensorElementType.Int64))
                throw new NotSupportedException("This ONNX voice isn't stored as float32 (it is fp16 or another type). Use the voice's .pth file instead.");
        const int frames = ProbeFrames;
        var names = model.InputNames;
        var ins = new List<OrtValue>();
        try
        {
            foreach (var n in names)
            {
                var meta = model.InputMetadata[n];
                long[] shape = n switch
                {
                    "phone" or "feats" => new long[] { 1, frames, ContentEncoder.Dim },
                    "rnd" or "z_p" => new long[] { 1, RvcGenerator.Inter, frames },
                    "pitch" or "pitchf" or "nsff0" => new long[] { 1, frames },
                    "g" => new long[] { 1, RvcGenerator.Gin, 1 },
                    _ => new long[] { 1 },
                };
                long count = shape.Aggregate(1L, (a, b) => a * b);
                if (meta.ElementDataType == TensorElementType.Int64)
                {
                    var data = new long[count];
                    if (n is "phone_lengths" or "p_len" or "lengths") data[0] = frames;
                    if (n == "pitch") Array.Fill(data, 1L);
                    ins.Add(OrtValue.CreateTensorValueFromMemory(data, shape));
                }
                else ins.Add(OrtValue.CreateTensorValueFromMemory(new float[count], shape));
            }
            using var r = model.Run(names.ToArray(), ins.ToArray(), new[] { model.OutputNames[0] });
            var outp = r.First().GetTensorDataAsSpan<float>();
            foreach (var v in outp)
                if (!float.IsFinite(v)) throw new InvalidDataException("The voice's model gives broken audio.");
            return outp.Length;
        }
        finally
        {
            foreach (var v in ins) v.Dispose();
        }
    }

    private static int Rate(int hop) => hop switch
    {
        320 => 32000,
        400 => 40000,
        480 => 48000,
        _ => throw new NotSupportedException($"This voice's sample rate isn't supported ({hop * 100} Hz)."),
    };
}
