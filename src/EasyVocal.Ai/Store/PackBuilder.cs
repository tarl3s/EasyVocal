using System.Text.Json;
using EasyVocal.Ai.Import;

namespace EasyVocal.Ai.Store;

/// <summary>The bundled voices' recipe (<c>Voices/bundled-voices.json</c>, embedded): blends of the base model's speakers.</summary>
public sealed class BundledRecipe
{
    public string Base { get; set; } = BaseModels.BaseGeneratorId;
    public string BaseSha256 { get; set; } = "";
    public string Source { get; set; } = "";
    public string Licence { get; set; } = "";
    public string Attribution { get; set; } = "";
    public List<Entry> Voices { get; set; } = new();

    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Emoji { get; set; } = "🧠";
        public double TargetHz { get; set; }
        public string? Description { get; set; }
        public List<double[]> Mix { get; set; } = new();
    }

    public static BundledRecipe Embedded()
    {
        using var s = typeof(BundledRecipe).Assembly.GetManifestResourceStream("EasyVocal.Ai.Voices.bundled-voices.json")
                      ?? throw new InvalidOperationException("Missing bundled-voices.json");
        return JsonSerializer.Deserialize<BundledRecipe>(s, VoiceManifest.Json) ?? throw new InvalidDataException("Empty recipe.");
    }
}

/// <summary>
/// Makes an AI pack folder from the public upstream files (<see cref="BaseModels"/>) with no Python: the content
/// encoder from HuBERT's weights and the embedded weight-less template, RMVPE and the base generator copied, the
/// bundled voices written from their recipe, RMVPE with its weights stored as fp16. The int8 content encoder (made once with Python, see
/// <c>tools/ai/export/variants.py</c>) can be added from a file. The release's pack (phase 4) is built this way.
/// </summary>
public static class PackBuilder
{
    /// <summary>The fp32 content encoder: HuBERT's state dict poured into the template.</summary>
    public static byte[] BuildContentEncoder(string hubertStateDict)
    {
        var ck = TorchCheckpoint.Load(hubertStateDict);
        var map = new WeightMap(ck.Tensors);
        var used = new HashSet<string>(StringComparer.Ordinal);
        using var s = typeof(PackBuilder).Assembly.GetManifestResourceStream("EasyVocal.Ai.Templates.hubert_base.onnx.gz")
                      ?? throw new InvalidOperationException("Missing the content encoder's template.");
        var ms = new MemoryStream();
        s.CopyTo(ms);
        var model = OnnxRewriter.Fill(OnnxRewriter.Unzip(ms.ToArray()), n => map.Find(n, used));
        var unused = map.Names.Where(k => !used.Contains(k) && !k.StartsWith("final_proj", StringComparison.Ordinal) && k != "masked_spec_embed" && !k.StartsWith("label_embs", StringComparison.Ordinal)).ToList();
        if (unused.Count > 0) throw new InvalidDataException("The content encoder's weights don't fit its template: " + string.Join(", ", unused.Take(3)));
        return model;
    }

    /// <summary>
    /// Writes the bundled voices into <paramref name="voicesFolder"/> (each <c>&lt;id&gt;/voice.json</c> + <c>speaker.f32</c>)
    /// from the base generator's speakers. Returns their manifests.
    /// </summary>
    public static IReadOnlyList<VoiceManifest> WriteBundledVoices(string basePth, string voicesFolder, BundledRecipe? recipe = null)
    {
        recipe ??= BundledRecipe.Embedded();
        var ck = TorchCheckpoint.Load(basePth);
        if (!ck.Tensors.TryGetValue("emb_g.weight", out var table)) throw new InvalidDataException("The base model has no speakers.");
        var list = new List<VoiceManifest>();
        foreach (var e in recipe.Voices)
        {
            double sum = e.Mix.Sum(x => x[1]);
            if (e.Mix.Count == 0 || Math.Abs(sum - 1) > 1e-6) throw new InvalidDataException($"{e.Id}: the weights must add up to 1.");
            var g = RvcTemplates.Blend(table, e.Mix.Select(x => ((int)x[0], x[1])));
            var dir = Path.Combine(voicesFolder, e.Id);
            Directory.CreateDirectory(dir);
            var spk = Path.Combine(dir, "speaker.f32");
            AiVoiceService.WriteSpeaker(spk, g);
            var m = new VoiceManifest
            {
                Id = e.Id, Name = e.Name, Emoji = e.Emoji, Kind = VoiceKinds.Speaker, Base = recipe.Base, SampleRate = 40000,
                TargetHz = e.TargetHz > 0 ? e.TargetHz : null, BuiltIn = true, Description = e.Description,
                Source = recipe.Source + ": " + string.Join(" + ", e.Mix.Select(x => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x[1]:0.##}×speaker {(int)x[0]}"))),
                Licence = recipe.Licence, Attribution = recipe.Attribution,
                Files = { new VoiceFile { Role = "speaker", Name = "speaker.f32", Size = new FileInfo(spk).Length, Sha256 = Downloader.Sha256Of(spk) } },
            };
            m.Write(Path.Combine(dir, VoiceManifest.FileName));
            list.Add(m);
        }
        return list;
    }

    /// <param name="baseFiles">Local paths of <see cref="BaseModels"/> by id (see <see cref="BaseModels.FetchAsync"/>).</param>
    /// <param name="int8Content">An int8 content encoder to add (optional).</param>
    public static void Build(AiPack pack, IReadOnlyDictionary<string, string> baseFiles, string? int8Content = null, IProgress<string>? log = null)
    {
        Directory.CreateDirectory(pack.EngineFolder);
        Directory.CreateDirectory(pack.VoicesFolder);
        void Put(string file, Action<string> write)
        {
            var dst = Path.Combine(pack.EngineFolder, file);
            var tmp = dst + ".tmp";
            write(tmp);
            File.Move(tmp, dst, overwrite: true);
            log?.Report("wrote " + file);
        }
        Put(AiPack.ContentFp32, t => File.WriteAllBytes(t, BuildContentEncoder(baseFiles[BaseModels.Hubert.Id])));
        // RMVPE's weights as fp16: half the download, expanded back to fp32 when it is loaded (AiPack.ReadPitchModel)
        if (baseFiles.TryGetValue(BaseModels.Rmvpe.Id, out var rmvpe)) Put(AiPack.RmvpeF16, t => File.WriteAllBytes(t, OnnxRewriter.StoreHalf(File.ReadAllBytes(rmvpe))));
        Put(BaseModels.BaseGeneratorId + ".pth", t => File.Copy(baseFiles[BaseModels.Generator40k.Id], t, true));
        if (int8Content != null) Put(AiPack.ContentInt8, t => File.Copy(int8Content, t, true));
        WriteBundledVoices(Path.Combine(pack.EngineFolder, BaseModels.BaseGeneratorId + ".pth"), pack.VoicesFolder);
        log?.Report("wrote the bundled voices");
    }
}
