namespace EasyVocal.Ai.Import;

/// <summary>
/// A checkpoint's state dict turned into the tensors a template is named after: weight norm folded
/// (<c>weight = g · v / ‖v‖</c>, both the old <c>weight_g</c>/<c>weight_v</c> names and the newer
/// <c>parametrizations.weight.original0/1</c>, along whichever dimension <c>g</c> keeps), training-only parts dropped,
/// and names matched even when one side has an extra leading prefix (an export wrapper's <c>m.</c>, a <c>hubert.</c>).
/// </summary>
public sealed class WeightMap
{
    private readonly Dictionary<string, Weight> byName = new(StringComparer.Ordinal);

    public WeightMap(IReadOnlyDictionary<string, Weight> stateDict, Func<string, bool>? drop = null)
    {
        foreach (var (k, v) in stateDict)
        {
            if (drop != null && drop(k)) continue;
            if (Split(k, out var baseName, out bool isG, out string other))
            {
                if (!isG) continue; // handled with its g
                if (!stateDict.TryGetValue(other, out var vv)) throw new InvalidDataException($"'{k}' has no matching '{other}'.");
                byName[baseName] = new Weight(Fold(v, vv), vv.Shape);
            }
            else byName[k] = v;
        }
    }

    public IReadOnlyCollection<string> Names => byName.Keys;

    public bool TryGet(string name, out Weight w) => byName.TryGetValue(name, out w!);

    /// <summary>
    /// The weight for a template's initializer name: the same name, else the same name with leading dotted parts
    /// taken off either side (<c>m.encoder.x</c> ↔ <c>encoder.x</c> ↔ <c>hubert.encoder.x</c>). Records the state-dict
    /// name used in <paramref name="used"/>.
    /// </summary>
    public Weight? Find(string templateName, ISet<string>? used = null)
    {
        if (byName.TryGetValue(templateName, out var w)) { used?.Add(templateName); return w; }
        for (string t = templateName; ;)
        {
            int dot = t.IndexOf('.');
            if (dot < 0) break;
            t = t[(dot + 1)..];
            if (byName.TryGetValue(t, out w)) { used?.Add(t); return w; }
        }
        foreach (var k in byName.Keys)
            if (k.EndsWith("." + templateName, StringComparison.Ordinal)) { used?.Add(k); return byName[k]; }
        return null;
    }

    private static bool Split(string k, out string baseName, out bool isG, out string other)
    {
        const string G = ".weight_g", V = ".weight_v", P0 = ".parametrizations.weight.original0", P1 = ".parametrizations.weight.original1";
        if (k.EndsWith(G, StringComparison.Ordinal)) { baseName = k[..^G.Length] + ".weight"; isG = true; other = k[..^G.Length] + V; return true; }
        if (k.EndsWith(V, StringComparison.Ordinal)) { baseName = k[..^V.Length] + ".weight"; isG = false; other = ""; return true; }
        if (k.EndsWith(P0, StringComparison.Ordinal)) { baseName = k[..^P0.Length] + ".weight"; isG = true; other = k[..^P0.Length] + P1; return true; }
        if (k.EndsWith(P1, StringComparison.Ordinal)) { baseName = k[..^P1.Length] + ".weight"; isG = false; other = ""; return true; }
        baseName = other = ""; isG = false;
        return false;
    }

    /// <summary>g · v / ‖v‖, the norm taken over every dimension but the one g keeps (all of them when g is one number).</summary>
    public static float[] Fold(Weight g, Weight v)
    {
        var shape = v.Shape;
        int keep = -1;
        if (g.Count > 1)
        {
            for (int d = 0; d < shape.Length; d++)
                if (shape[d] == g.Count && (g.Shape.Length != shape.Length || g.Shape[d] == g.Count)) { keep = d; break; }
            if (keep < 0) throw new InvalidDataException("A weight norm whose shapes don't match.");
        }
        long n = v.Count;
        long stride = 1;
        for (int d = keep + 1; d < shape.Length && keep >= 0; d++) stride *= shape[d];
        long size = keep >= 0 ? shape[keep] : 1;
        var ss = new double[size];
        for (long i = 0; i < n; i++)
        {
            long c = keep >= 0 ? i / stride % size : 0;
            double x = v.Data[i];
            ss[c] += x * x;
        }
        var scale = new float[size];
        for (long c = 0; c < size; c++) scale[c] = (float)(g.Data[c] / Math.Sqrt(Math.Max(ss[c], 1e-30)));
        var w = new float[n];
        for (long i = 0; i < n; i++)
        {
            long c = keep >= 0 ? i / stride % size : 0;
            w[i] = v.Data[i] * scale[c];
        }
        return w;
    }
}
