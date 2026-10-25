using EasyVocal.Ai.Import;
using EasyVocal.Ai.Rvc;
using EasyVocal.Ai.Runtime;
using EasyVocal.Ai.Tests.Helpers;
using Microsoft.ML.OnnxRuntime;
using Xunit.Abstractions;

namespace EasyVocal.Ai.Tests;

/// <summary>.pth / ONNX / .index reading on the tiny fixtures: no PyTorch, no faiss, the same numbers as they give.</summary>
public class ImportTests
{
    private readonly ITestOutputHelper output;

    public ImportTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void A_pth_is_read_like_PyTorch_wrote_it()
    {
        var ck = TorchCheckpoint.Load(AiFixtures.P("tiny.pth"));
        Assert.Equal("40k", ck.Sr);
        Assert.Equal("v2", ck.Version);
        Assert.True(ck.HasPitch);
        Assert.Equal("tiny test model", ck.Info);
        Assert.Equal(18, ck.Config!.Count);
        Assert.Equal(new long[] { 2, 256 }, ck.Tensors["emb_g.weight"].Shape);
        Assert.Contains("dec.ups.0.weight_g", ck.Tensors.Keys);
        Assert.DoesNotContain(ck.Tensors.Keys, k => k.StartsWith("enc_q."));
        Assert.All(ck.Tensors.Values, t => Assert.All(t.Data, v => Assert.True(float.IsFinite(v))));
    }

    [Fact]
    public void Weight_norm_folds_along_the_dimension_g_keeps()
    {
        // dim 0 (RVC's convolutions): one norm per output row
        var v = new Weight(new float[] { 3, 4, 0, 5 }, new long[] { 2, 2, 1 });
        var g = new Weight(new float[] { 10, 2 }, new long[] { 2, 1, 1 });
        Assert.Equal(new float[] { 6, 8, 0, 2 }, WeightMap.Fold(g, v));
        // dim 2 (HuBERT's positional convolution): one norm per kernel tap
        var v2 = new Weight(new float[] { 3, 1, 4, 1 }, new long[] { 2, 1, 2 }); // taps: (3,4) and (1,1)
        var g2 = new Weight(new float[] { 5, 2 }, new long[] { 1, 1, 2 });
        var w2 = WeightMap.Fold(g2, v2);
        Assert.Equal(3f, w2[0], 4);
        Assert.Equal(4f, w2[2], 4);
        Assert.Equal((float)(2 / Math.Sqrt(2)), w2[1], 4);
        Assert.Equal((float)(2 / Math.Sqrt(2)), w2[3], 4);
    }

    [Fact]
    public void A_pth_through_its_template_sounds_like_PyTorchs_own_export()
    {
        var ck = TorchCheckpoint.Load(AiFixtures.P("tiny.pth"));
        var built = RvcTemplates.FromPth(ck, OnnxRewriter.ReadModel(AiFixtures.P("tiny_enc.onnx.gz")), OnnxRewriter.ReadModel(AiFixtures.P("tiny_dec.onnx.gz")));
        Assert.Equal(40000, built.SampleRate);
        Assert.NotNull(built.Speakers);

        var mine = Generate(new RvcGenerator(40000, OrtRuntime.Create("t enc", built.Enc, AiDevice.Cpu, 1), OrtRuntime.Create("t dec", built.Dec, AiDevice.Cpu, 1)));
        var theirs = Generate(new RvcGenerator(40000,
            OrtRuntime.CreateFromFile("ref enc", AiFixtures.P("tiny_ref_enc.onnx"), AiDevice.Cpu, 1),
            OrtRuntime.CreateFromFile("ref dec", AiFixtures.P("tiny_ref_dec.onnx"), AiDevice.Cpu, 1)));
        double corr = Corr(mine, theirs), rms = Rms(theirs);
        output.WriteLine($"template + .pth vs PyTorch export: correlation {corr:0.00000}, rms {Rms(mine):0.0000} vs {rms:0.0000}");
        Assert.True(rms > 1e-4, "the reference makes sound");
        Assert.True(corr > 0.999, $"correlation {corr}");
    }

    /// <summary>The same inputs (fixed seed: same noise) through a generator.</summary>
    private static float[] Generate(RvcGenerator g)
    {
        using (g)
        using (var run = g.NewRun(40, 20, seed: 9))
        {
            var rng = new Random(3);
            for (int i = 0; i < run.Phone.Length; i++) run.Phone[i] = (float)(rng.NextDouble() - 0.5);
            for (int i = 0; i < 40; i++) { run.F0[i] = 140 + i; run.Coarse[i] = F0.Coarse(run.F0[i]); }
            for (int i = 0; i < run.Speaker.Length; i++) run.Speaker[i] = (float)Math.Sin(i * 0.1);
            run.Generate();
            return run.Audio.ToArray();
        }
    }

    [Fact]
    public void A_pth_with_parts_the_template_doesnt_have_is_refused_clearly()
    {
        var ck = TorchCheckpoint.Load(AiFixtures.P("tiny.pth"));
        ck.Tensors["dec.refine_gan.extra.weight"] = new Weight(new float[4], new long[] { 4 });
        var ex = Assert.Throws<NotSupportedException>(() => RvcTemplates.FromPth(ck,
            OnnxRewriter.ReadModel(AiFixtures.P("tiny_enc.onnx.gz")), OnnxRewriter.ReadModel(AiFixtures.P("tiny_dec.onnx.gz"))));
        Assert.Contains("isn't supported yet", ex.Message);

        // the real 40k template refuses the tiny network's shapes
        var ck2 = TorchCheckpoint.Load(AiFixtures.P("tiny.pth"));
        Assert.Throws<NotSupportedException>(() => RvcTemplates.FromPth(ck2));
    }

    [Fact]
    public void Garbage_is_refused_as_damaged_not_crashed_on()
    {
        var dir = EasyVocal.Tests.Helpers.TestFolders.New("EV_AiImport_");
        try
        {
            var junk = Path.Combine(dir, "junk.pth");
            File.WriteAllBytes(junk, Enumerable.Range(0, 5000).Select(i => (byte)(i * 37)).ToArray());
            Assert.Throws<InvalidDataException>(() => TorchCheckpoint.Load(junk));
            // a cut-short pickle inside a valid zip
            var cut = Path.Combine(dir, "cut.pth");
            using (var z = System.IO.Compression.ZipFile.Open(cut, System.IO.Compression.ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("x/data.pkl");
                using var s = e.Open();
                s.Write(new byte[] { 0x80, 0x02, 0x7d, 0x71, 0x00, 0x58, 0x10, 0, 0, 0, (byte)'a' });
            }
            Assert.Throws<InvalidDataException>(() => TorchCheckpoint.Load(cut));
            Assert.Throws<InvalidDataException>(() => OnnxRewriter.Initializers(new byte[] { 0x3a, 0xff, 0xff, 0xff, 0x0f, 1, 2 }));
        }
        finally { EasyVocal.Tests.Helpers.TestFolders.Delete(dir); }
    }

    [Fact]
    public void The_index_reader_blends_like_RVC_with_faiss()
    {
        var ix = FeatureIndex.Load(AiFixtures.P("tiny.index"));
        Assert.Equal(768, ix.D);
        Assert.Equal(160, ix.NTotal);
        Assert.Equal(1, ix.NProbe);
        var q = ReadF32(AiFixtures.P("tiny_index_q.f32"));
        var want = ReadF32(AiFixtures.P("tiny_index_blend.f32"));
        ix.Blend(q, 0, q.Length / 768, 0.75f);
        double maxErr = 0;
        for (int i = 0; i < q.Length; i++) maxErr = Math.Max(maxErr, Math.Abs(q[i] - want[i]));
        output.WriteLine($"max difference to faiss + numpy: {maxErr:E2}");
        Assert.True(maxErr < 1e-3, $"max error {maxErr}");
    }

    [Fact]
    public void The_content_encoders_frame_count_matches_the_model()
    {
        using var m = OrtRuntime.CreateFromFile("tiny content", AiFixtures.P("tiny_hubert.onnx"), AiDevice.Cpu, 1);
        foreach (int s in new[] { 3200, 9600, 16000, 25760 })
        {
            using var x = OrtValue.CreateTensorValueFromMemory(new float[s], new long[] { 1, s });
            using var r = m.Run(new[] { "source" }, new[] { x }, new[] { m.OutputNames[0] });
            var shape = r.First().GetTensorTypeAndShape().Shape;
            Assert.Equal(ContentEncoder.HubertFrames(s), (int)shape[1]);
        }
    }

    [Fact]
    public void Rewriting_keeps_constants_and_lists_initializers()
    {
        var tpl = OnnxRewriter.ReadModel(AiFixtures.P("tiny_dec.onnx.gz"));
        var inits = OnnxRewriter.Initializers(tpl);
        Assert.Contains(inits, i => i.Name == "dec.conv_pre.weight" && !i.HasData);
        Assert.Equal(new[] { "z_p", "nsff0", "g" }, OnnxRewriter.InputNames(tpl).Take(3));
        var single = File.ReadAllBytes(AiFixtures.P("tiny_single.onnx"));
        Assert.Equal(new[] { "phone", "phone_lengths", "pitch", "pitchf", "ds", "rnd" }, OnnxRewriter.InputNames(single));
    }

    [Fact]
    public void A_single_graph_voice_runs()
    {
        // the same network in RVC WebUI's one-graph layout: it decodes every frame, the newest are kept
        using var single = new RvcGenerator(40000, OrtRuntime.CreateFromFile("single", AiFixtures.P("tiny_single.onnx"), AiDevice.Cpu, 1));
        using var run = single.NewRun(40, 20, seed: 9);
        var rng = new Random(3);
        for (int i = 0; i < run.Phone.Length; i++) run.Phone[i] = (float)(rng.NextDouble() - 0.5);
        for (int i = 0; i < 40; i++) { run.F0[i] = 150; run.Coarse[i] = F0.Coarse(150); }
        run.Generate();
        Assert.Equal(20 * 400, run.Audio.Length);
        Assert.All(run.Audio, v => Assert.True(float.IsFinite(v)));
        Assert.True(Rms(run.Audio) > 1e-4);
    }

    private static float[] ReadF32(string path)
    {
        var b = File.ReadAllBytes(path);
        var f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, b.Length);
        return f;
    }

    internal static double Corr(float[] a, float[] b)
    {
        double s = 0, aa = 0, bb = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) { s += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return s / Math.Sqrt(aa * bb + 1e-30);
    }

    internal static double Rms(float[] a) => Math.Sqrt(a.Sum(v => (double)v * v) / Math.Max(1, a.Length));
}
