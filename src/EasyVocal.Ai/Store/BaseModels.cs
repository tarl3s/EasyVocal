namespace EasyVocal.Ai.Store;

/// <summary>A public model file the AI pack is made from (all MIT, from RVC's own Hugging Face repository).</summary>
public sealed record BaseFile(string Id, string Url, string Sha256, long Size, string Licence);

/// <summary>
/// The upstream files the pack's engine models come from. Everything is checked by SHA-256 (the values are the
/// files' Git LFS ids on Hugging Face). <see cref="PackBuilder"/> turns them into the pack's engine folder with no
/// Python: the content encoder is HuBERT's weights poured into a weight-less ONNX template.
/// </summary>
public static class BaseModels
{
    private const string Repo = "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/main/";
    private const string Mit = "MIT (RVC-Project, lj1995/VoiceConversionWebUI)";

    /// <summary>RVC's HuBERT base in Transformers format (fp16 state dict, 180 MB).</summary>
    public static readonly BaseFile Hubert = new("hubert_base/pytorch_model.bin", Repo + "hubert_base/pytorch_model.bin",
        "cc8c20f4b90a520757260197a3ff2505705a7adbd20ad9eeaa4e1a9b38442ef5", 189206711, Mit + "; HuBERT: fairseq, MIT, trained on LibriSpeech (CC BY 4.0)");

    /// <summary>RMVPE pitch model, ONNX (345 MB).</summary>
    public static readonly BaseFile Rmvpe = new("rmvpe.onnx", Repo + "rmvpe.onnx",
        "5370e71ac80af8b4b7c793d27efd51fd8bf962de3a7ede0766dac0befa3660fd", 361688443, Mit + "; RMVPE: Apache-2.0");

    /// <summary>RVC v2 40k base generator with pitch (70 MB): 109 VCTK speakers.</summary>
    public static readonly BaseFile Generator40k = new("pretrained_v2/f0G40k.pth", Repo + "pretrained_v2/f0G40k.pth",
        "3b2c44035e782c4b14ddc0bede9e2f4a724d025cd073f736d4f43708453adfcb", 73106273,
        Mit + "; trained on the CSTR VCTK Corpus (CC BY 4.0)");

    public static IReadOnlyList<BaseFile> All { get; } = new[] { Hubert, Rmvpe, Generator40k };

    /// <summary>The generator id the bundled voices use (engine/<c>rvc-v2-40k.pth</c>).</summary>
    public const string BaseGeneratorId = "rvc-v2-40k";

    /// <summary>
    /// Makes sure every file is in <paramref name="cacheFolder"/> (downloading what is missing or different), and
    /// returns the local paths by id.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> FetchAsync(string cacheFolder, IEnumerable<BaseFile>? files = null,
        IProgress<(BaseFile File, DownloadProgress Progress)>? progress = null, CancellationToken ct = default)
    {
        var paths = new Dictionary<string, string>();
        foreach (var f in files ?? All)
        {
            var local = Path.Combine(cacheFolder, f.Id.Replace('/', Path.DirectorySeparatorChar));
            if (!Verified(local, f))
            {
                var p = progress == null ? null : new InlineProgress<DownloadProgress>(d => progress.Report((f, d)));
                await Downloader.DownloadAsync(new Uri(f.Url), local, f.Size + 1, f.Sha256, p, ct).ConfigureAwait(false);
                File.WriteAllText(local + ".sha256", f.Sha256);
            }
            paths[f.Id] = local;
        }
        return paths;
    }

    /// <summary>The file is there and was checked (a <c>.sha256</c> note newer than it saves hashing it again).</summary>
    private static bool Verified(string local, BaseFile f)
    {
        if (!File.Exists(local) || new FileInfo(local).Length != f.Size) return false;
        var note = local + ".sha256";
        if (File.Exists(note) && File.GetLastWriteTimeUtc(note) >= File.GetLastWriteTimeUtc(local) && File.ReadAllText(note).Trim() == f.Sha256) return true;
        if (Downloader.Sha256Of(local) != f.Sha256) return false;
        File.WriteAllText(note, f.Sha256);
        return true;
    }
}
