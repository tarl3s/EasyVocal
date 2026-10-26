using EasyVocal.Ai.Store;

namespace EasyVocal.Ai.Tests.Helpers;

/// <summary>
/// The real models for the tests that need them: RVC's public MIT files downloaded once into a cache folder
/// (<c>EV_AI_MODELS</c>, else %TEMP%\EV_AiModelCache; kept between runs, CI caches it) and an AI pack built from them
/// there by <see cref="PackBuilder"/> (with the bundled voices). An int8 content encoder is added when the cache has
/// one (<c>hubert_int8.onnx</c>, made with tools/ai/export/variants.py) or <c>EV_AI_INT8</c> points to one.
/// <c>EV_AI_OFFLINE=1</c> never downloads. When the models can't be had the tests say "SKIPPED" and why.
/// </summary>
public static class RealModels
{
    private const string Stamp = ".built-v1";
    private static readonly Lazy<(string? Data, string? Skip)> Built = new(Build);

    public static string Cache => Environment.GetEnvironmentVariable("EV_AI_MODELS") is { Length: > 0 } d ? d
        : Path.Combine(Path.GetTempPath(), "EV_AiModelCache");

    /// <summary>The data folder holding the real AI pack, or null (then <paramref name="skip"/> says why).</summary>
    public static string? DataFolder(out string? skip)
    {
        var (data, why) = Built.Value;
        skip = why;
        // CI must have them (it caches RVC's files): a missing model there is a failure, not a skip.
        if (data == null && OnCi && Environment.GetEnvironmentVariable("EV_AI_OFFLINE") != "1")
            throw new InvalidOperationException("CI must test with the real AI models: " + why);
        return data;
    }

    /// <summary>On a CI runner (GitHub Actions sets CI=true): timing budgets are reported, not enforced.</summary>
    public static bool OnCi => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    private static (string?, string?) Build()
    {
        try
        {
            var data = Path.Combine(Cache, "data");
            var pack = new AiPack(data);
            string stamp = Path.Combine(pack.Root, Stamp);
            string recipe = System.Text.Json.JsonSerializer.Serialize(BundledRecipe.Embedded().Voices);
            if (File.Exists(stamp) && pack.Installed)
            {
                if (File.ReadAllText(stamp) != recipe)
                {
                    // the bundled voices changed: write them again (the models stay)
                    foreach (var v in BundledRecipe.Embedded().Voices) Core.Storage.Cleanup.TryDelete(pack.VoiceFolder(v.Id));
                    PackBuilder.WriteBundledVoices(Path.Combine(pack.EngineFolder, BaseModels.BaseGeneratorId + ".pth"), pack.VoicesFolder);
                    foreach (var dir in Directory.GetDirectories(pack.VoicesFolder))
                        if (File.Exists(Path.Combine(dir, VoiceManifest.FileName)) && VoiceManifest.Read(Path.Combine(dir, VoiceManifest.FileName)).BuiltIn
                            && !BundledRecipe.Embedded().Voices.Any(v => v.Id == Path.GetFileName(dir)))
                            Core.Storage.Cleanup.TryDelete(dir);
                    File.WriteAllText(stamp, recipe);
                }
                return (data, null);
            }
            bool offline = Environment.GetEnvironmentVariable("EV_AI_OFFLINE") == "1";
            var upstream = Path.Combine(Cache, "upstream");
            if (offline && BaseModels.All.Any(f => !File.Exists(Path.Combine(upstream, f.Id.Replace('/', Path.DirectorySeparatorChar)))))
                return (null, "the real AI models aren't in " + upstream + " and EV_AI_OFFLINE=1");
            var files = BaseModels.FetchAsync(upstream).GetAwaiter().GetResult();
            string? int8 = Environment.GetEnvironmentVariable("EV_AI_INT8") is { Length: > 0 } e && File.Exists(e) ? e
                : File.Exists(Path.Combine(Cache, "hubert_int8.onnx")) ? Path.Combine(Cache, "hubert_int8.onnx") : null;
            if (Directory.Exists(pack.Root)) Directory.Delete(pack.Root, recursive: true);
            PackBuilder.Build(pack, files, int8);
            File.WriteAllText(stamp, recipe);
            return (data, null);
        }
        catch (Exception ex)
        {
            return (null, "the real AI models couldn't be had: " + ex.Message);
        }
    }
}
