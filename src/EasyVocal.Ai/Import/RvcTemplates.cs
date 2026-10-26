namespace EasyVocal.Ai.Import;

/// <summary>A generator ready to load: its two ONNX graphs (weights inside) and the speakers it was trained with.</summary>
public sealed record BuiltGenerator(byte[] Enc, byte[] Dec, int SampleRate, Weight? Speakers);

/// <summary>
/// The weight-less RVC v2 templates shipped inside this assembly (one per output rate, exported once by
/// <c>tools/ai/export/export_rvc.py</c>) and how a <c>.pth</c> becomes a generator through them.
/// </summary>
public static class RvcTemplates
{
    public static IReadOnlyList<int> Rates { get; } = new[] { 32000, 40000, 48000 };

    private static byte[] Resource(string name)
    {
        using var s = typeof(RvcTemplates).Assembly.GetManifestResourceStream("EasyVocal.Ai.Templates." + name)
                      ?? throw new InvalidOperationException("Missing template " + name);
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return OnnxRewriter.Unzip(ms.ToArray());
    }

    public static byte[] Enc(int sr) => Resource($"rvc_v2_f0_{sr / 1000}k_enc.onnx.gz");
    public static byte[] Dec(int sr) => Resource($"rvc_v2_f0_{sr / 1000}k_dec.onnx.gz");

    /// <summary>
    /// The output rate a .pth says it has ("40k", or the config's last number); without either (RVC's pretrained
    /// files) the first upsampling layer's kernel tells (32k: 20, 40k: 16, 48k: 24); 0 = unknown.
    /// </summary>
    public static int SampleRateOf(TorchCheckpoint ck)
    {
        var s = ck.Sr?.Trim().ToLowerInvariant();
        if (s is "32k" or "32000") return 32000;
        if (s is "40k" or "40000") return 40000;
        if (s is "48k" or "48000") return 48000;
        if (ck.Config is { Count: > 0 } c && c[^1] is long l && (l == 32000 || l == 40000 || l == 48000)) return (int)l;
        foreach (var k in new[] { "dec.ups.0.weight_v", "dec.ups.0.parametrizations.weight.original1", "dec.ups.0.weight" })
            if (ck.Tensors.TryGetValue(k, out var w) && w.Shape.Length == 3)
                return w.Shape[2] switch { 20 => 32000, 16 => 40000, 24 => 48000, _ => 0 };
        return 0;
    }

    /// <summary>
    /// A .pth through its template: checks it is a kind EasyVocal runs (RVC v2 with pitch, 32/40/48 kHz) and gives
    /// a clear sentence when not; every tensor the template needs must be there with its shape, and every tensor of
    /// the voice must be used (else the layout differs: a fork, another vocoder).
    /// </summary>
    public static BuiltGenerator FromPth(TorchCheckpoint ck, byte[]? encTemplate = null, byte[]? decTemplate = null)
    {
        if (!ck.Tensors.TryGetValue("enc_p.emb_phone.weight", out var phone))
            throw new InvalidDataException("This isn't an RVC voice model (no content input layer). Training checkpoints (G_*.pth/D_*.pth) can't be used: pick the small exported voice .pth.");
        if (phone.Shape.Length != 2 || phone.Shape[1] != 768)
            throw new NotSupportedException("This is an RVC v1 voice (256-dim content). Only RVC v2 voices are supported for now.");
        if (!ck.HasPitch || !ck.Tensors.ContainsKey("enc_p.emb_pitch.weight"))
            throw new NotSupportedException("This voice was trained without pitch. Only RVC voices with pitch are supported for now.");
        int sr = SampleRateOf(ck);
        if (encTemplate == null && !Rates.Contains(sr))
            throw new NotSupportedException("This voice's sample rate isn't supported (only 32k, 40k and 48k).");
        var map = new WeightMap(ck.Tensors, k => k.StartsWith("enc_q.", StringComparison.Ordinal));
        var used = new HashSet<string>(StringComparer.Ordinal);
        byte[] enc, dec;
        try
        {
            enc = OnnxRewriter.Fill(encTemplate ?? Enc(sr), n => map.Find(n, used));
            dec = OnnxRewriter.Fill(decTemplate ?? Dec(sr), n => map.Find(n, used));
        }
        catch (InvalidDataException ex)
        {
            throw new NotSupportedException("This voice's model type isn't supported yet (" + ex.Message + ")", ex);
        }
        var unused = map.Names.Where(k => k != "emb_g.weight" && !used.Contains(k)).ToList();
        if (unused.Count > 0)
            throw new NotSupportedException("This voice's model type isn't supported yet (it has parts EasyVocal doesn't know: "
                                            + string.Join(", ", unused.Take(3)) + ").");
        map.TryGet("emb_g.weight", out var speakers);
        return new BuiltGenerator(enc, dec, sr, speakers);
    }

    /// <summary>Speaker <paramref name="index"/> of a generator's speaker table (256 values).</summary>
    public static float[] SpeakerVector(Weight table, int index)
    {
        int dim = (int)table.Shape[^1];
        int count = (int)(table.Count / dim);
        if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        return table.Data.AsSpan(index * dim, dim).ToArray();
    }

    /// <summary>A weighted blend of several speakers (a voice that is nobody in particular).</summary>
    public static float[] Blend(Weight table, IEnumerable<(int Speaker, double Weight)> mix)
    {
        int dim = (int)table.Shape[^1];
        var g = new float[dim];
        foreach (var (s, w) in mix)
        {
            var v = SpeakerVector(table, s);
            for (int i = 0; i < dim; i++) g[i] += (float)(v[i] * w);
        }
        return g;
    }
}
