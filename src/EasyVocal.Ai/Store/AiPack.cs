namespace EasyVocal.Ai.Store;

/// <summary>
/// The AI pack's folder inside the data folder (<c>&lt;data&gt;/AI</c>):
/// <code>
/// engine/hubert.onnx        content encoder, fp32 (MIT, RVC's HuBERT base)
/// engine/hubert_int8.onnx   the same, int8 (optional: used on the CPU when present)
/// engine/rmvpe_f16.onnx     pitch model, its weights stored as fp16 (expanded to fp32 at load: <see cref="ReadPitchModel"/>);
///                           or engine/rmvpe.onnx as published (optional: YIN at every level without either)
/// engine/rvc-v2-40k.pth     RVC v2 40k base generator (MIT; trained on VCTK, CC BY 4.0): the bundled voices' generator
/// runtime/                  ONNX Runtime's native library (optional: else .NET's own probing)
/// voices/&lt;id&gt;/voice.json  one folder per voice (see <see cref="VoiceManifest"/>)
/// </code>
/// </summary>
public sealed class AiPack
{
    public const string FolderName = "AI";
    public const string ContentFp32 = "hubert.onnx", ContentInt8 = "hubert_int8.onnx", Rmvpe = "rmvpe.onnx", RmvpeF16 = "rmvpe_f16.onnx";

    public AiPack(string dataFolder)
    {
        Root = Path.Combine(dataFolder, FolderName);
    }

    public string Root { get; }
    public string EngineFolder => Path.Combine(Root, "engine");
    public string VoicesFolder => Path.Combine(Root, "voices");
    public string RuntimeFolder => Path.Combine(Root, "runtime");

    /// <summary>The content encoder is there (the one part every AI voice needs).</summary>
    public bool Installed => File.Exists(Path.Combine(EngineFolder, ContentFp32)) || File.Exists(Path.Combine(EngineFolder, ContentInt8));

    /// <summary>The content encoder to use: int8 when wanted and present, else fp32, else int8; null = none.</summary>
    public string? ContentModel(bool preferInt8)
    {
        string fp = Path.Combine(EngineFolder, ContentFp32), i8 = Path.Combine(EngineFolder, ContentInt8);
        if (preferInt8 && File.Exists(i8)) return i8;
        if (File.Exists(fp)) return fp;
        return File.Exists(i8) ? i8 : null;
    }

    /// <summary>The pitch model's file (the fp16-stored one first), or null.</summary>
    public string? RmvpeModel => new[] { RmvpeF16, Rmvpe }.Select(f => Path.Combine(EngineFolder, f)).FirstOrDefault(File.Exists);

    /// <summary>
    /// A pitch model file as runnable ONNX bytes: <c>rmvpe_f16.onnx</c> keeps its weights as fp16 (172 MB instead of
    /// 345 MB in the download) and is expanded back to fp32 here (<see cref="Import.OnnxRewriter.ExpandHalf"/>).
    /// </summary>
    public static byte[] ReadPitchModel(string path) =>
        string.Equals(Path.GetFileName(path), RmvpeF16, StringComparison.OrdinalIgnoreCase)
            ? Import.OnnxRewriter.ExpandHalf(File.ReadAllBytes(path))
            : Import.OnnxRewriter.ReadModel(path);

    /// <summary>A base generator by id ("rvc-v2-40k" → engine/rvc-v2-40k.pth), or null when missing.</summary>
    public string? BaseGenerator(string id)
    {
        if (!Core.Storage.FileNames.IsPlain(id + ".pth")) return null;
        var p = Path.Combine(EngineFolder, id + ".pth");
        return File.Exists(p) ? p : null;
    }

    public string VoiceFolder(string id) => Path.Combine(VoicesFolder, id);

    /// <summary>
    /// The pack's own generator templates for a rate (engine/templates/rvc_v2_f0_40k_enc.onnx.gz…), which take
    /// precedence over the ones built into EasyVocal (a pack can bring newer ones); nulls when it has none.
    /// </summary>
    public (byte[]? Enc, byte[]? Dec) Templates(int sampleRate)
    {
        string dir = Path.Combine(EngineFolder, "templates"), k = $"rvc_v2_f0_{sampleRate / 1000}k_";
        string e = Path.Combine(dir, k + "enc.onnx.gz"), d = Path.Combine(dir, k + "dec.onnx.gz");
        return File.Exists(e) && File.Exists(d) ? (Import.OnnxRewriter.ReadModel(e), Import.OnnxRewriter.ReadModel(d)) : (null, null);
    }

    /// <summary>A .pth through this pack's (or the built-in) template.</summary>
    public Import.BuiltGenerator BuildPth(Import.TorchCheckpoint ck)
    {
        var (enc, dec) = Templates(Import.RvcTemplates.SampleRateOf(ck));
        return Import.RvcTemplates.FromPth(ck, enc, dec);
    }
}
