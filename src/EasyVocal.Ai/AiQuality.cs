using EasyVocal.Ai.Rvc;

namespace EasyVocal.Ai;

/// <summary>Realism against load and delay (design: Light / Balanced / Max).</summary>
public enum AiQuality { Light, Balanced, Max }

/// <summary>What each level sets (phase 1 research, measured on an i5-10400F: Light ~357 ms, Balanced ~430, Max ~590 on the CPU).</summary>
public static class AiLevels
{
    public static IReadOnlyList<AiQuality> All { get; } = new[] { AiQuality.Light, AiQuality.Balanced, AiQuality.Max };

    public static string Id(AiQuality q) => q switch { AiQuality.Light => "light", AiQuality.Max => "max", _ => "balanced" };

    /// <summary>"light" / "balanced" / "max" (any case); anything else = null.</summary>
    public static AiQuality? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "light" => AiQuality.Light,
        "balanced" => AiQuality.Balanced,
        "max" => AiQuality.Max,
        _ => null,
    };

    /// <param name="gpu">The generator runs on a GPU (smaller blocks pay off there).</param>
    /// <param name="singleGraph">The voice is one graph that decodes all its context: keep the context short.</param>
    public static StreamSettings Settings(AiQuality q, bool gpu, bool singleGraph = false)
    {
        var s = q switch
        {
            AiQuality.Light => new StreamSettings(200, 400, 20, UseRmvpe: false, IndexRate: 0),
            AiQuality.Max => new StreamSettings(gpu ? 150 : 250, 2000, 50, UseRmvpe: true, IndexRate: 0.75f),
            _ => new StreamSettings(gpu ? 120 : 200, 1000, 40, UseRmvpe: true, IndexRate: 0.5f),
        };
        if (singleGraph) s = s with { ExtraMs = Math.Min(s.ExtraMs, 300) };
        return s;
    }

    /// <summary>Content encoder precision: int8 on the CPU (measured 20-25 % faster, similarity 0.999) except at Max.</summary>
    public static bool PrefersInt8Content(AiQuality q, bool gpu) => !gpu && q != AiQuality.Max;

    /// <summary>
    /// How long a block is assumed to take before it was measured on this PC (ms, 95th percentile): the stream starts
    /// with that much cushion and grows it if it runs late.
    /// </summary>
    public static double DefaultBlockMs(AiQuality q) => q switch { AiQuality.Light => 120, AiQuality.Max => 270, _ => 170 };

    /// <summary>
    /// The cushion a stream starts with for a block time (95th percentile, ms): 25 % more plus 15 ms for the rest of the
    /// PC; an under-run grows it by 20 ms anyway.
    /// </summary>
    public static double CushionFor(double blockMsP95) => blockMsP95 * 1.25 + 15;
}
