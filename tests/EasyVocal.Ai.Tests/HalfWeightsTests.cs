using EasyVocal.Ai.Import;
using EasyVocal.Ai.Rvc;
using EasyVocal.Ai.Runtime;
using EasyVocal.Ai.Store;
using EasyVocal.Ai.Tests.Helpers;
using EasyVocal.Tests.Helpers;
using Xunit.Abstractions;

namespace EasyVocal.Ai.Tests;

/// <summary>
/// The pitch model ships with its weights stored as fp16 (<see cref="OnnxRewriter.StoreHalf"/>) and is expanded back to
/// fp32 when loaded (<see cref="OnnxRewriter.ExpandHalf"/>, <see cref="AiPack.ReadPitchModel"/>): half the bytes, the
/// same graph, the same pitch.
/// </summary>
public class HalfWeightsTests
{
    private readonly ITestOutputHelper output;

    public HalfWeightsTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void Weights_stored_as_fp16_are_half_the_size_and_expand_into_the_same_graph()
    {
        var model = File.ReadAllBytes(AiFixtures.P("tiny_rmvpe.onnx"));
        var stored = OnnxRewriter.StoreHalf(model, minElements: 64);
        var expanded = OnnxRewriter.ExpandHalf(stored);
        var before = OnnxRewriter.Initializers(model);
        var half = OnnxRewriter.Initializers(stored);
        var after = OnnxRewriter.Initializers(expanded);
        output.WriteLine($"{model.Length} bytes -> stored {stored.Length} -> expanded {expanded.Length}; {half.Count(i => i.DataType == 10)}/{half.Count} initializers fp16");
        Assert.Equal(before.Select(i => (i.Name, string.Join(",", i.Dims))), after.Select(i => (i.Name, string.Join(",", i.Dims))));
        Assert.Contains(half, i => i.DataType == 10);
        Assert.All(half.Where(i => i.DataType == 10), i => Assert.True(i.Dims.Aggregate(1L, (a, b) => a * b) >= 64, i.Name + " is small: it stays fp32"));
        Assert.All(after, i => Assert.NotEqual(10, i.DataType));
        Assert.True(stored.Length < model.Length * 0.6, $"stored {stored.Length} of {model.Length} bytes");
        Assert.True(Math.Abs(expanded.Length - model.Length) < model.Length / 50, "the expanded model is as big as the original");
        Assert.Throws<InvalidDataException>(() => OnnxRewriter.StoreHalf(stored)); // already fp16: refused
        Assert.Equal(expanded, OnnxRewriter.ExpandHalf(OnnxRewriter.StoreHalf(expanded, minElements: 64))); // fp16 values survive a second trip exactly
    }

    [Fact]
    public void The_expanded_pitch_model_gives_the_same_output()
    {
        var model = File.ReadAllBytes(AiFixtures.P("tiny_rmvpe.onnx"));
        var data = TestFolders.New("EV_AiHalf_");
        try
        {
            var pack = new AiPack(data);
            Directory.CreateDirectory(pack.EngineFolder);
            File.WriteAllBytes(Path.Combine(pack.EngineFolder, AiPack.Rmvpe), model);
            Assert.Equal(Path.Combine(pack.EngineFolder, AiPack.Rmvpe), pack.RmvpeModel);
            File.WriteAllBytes(Path.Combine(pack.EngineFolder, AiPack.RmvpeF16), OnnxRewriter.StoreHalf(model, minElements: 64));
            Assert.Equal(Path.Combine(pack.EngineFolder, AiPack.RmvpeF16), pack.RmvpeModel); // the fp16-stored one first

            var x = new float[16000];
            for (int i = 0; i < x.Length; i++) x[i] = 0.3f * MathF.Sin(2 * MathF.PI * 180 * i / 16000f) + 0.1f * MathF.Sin(2 * MathF.PI * 361 * i / 16000f);
            float[] Raw(byte[] onnx)
            {
                using var m = OrtRuntime.Create("pitch", onnx, AiDevice.Cpu, 1);
                var mel = new float[128 * 32];
                for (int i = 0; i < mel.Length; i++) mel[i] = MathF.Sin(i * 0.37f) * 2 - 3;
                using var input = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(mel, new long[] { 1, 128, 32 });
                using var outs = m.Run(new[] { "input" }, new[] { input }, new[] { m.OutputNames[0] });
                return outs[0].GetTensorDataAsSpan<float>().ToArray();
            }
            var a = Raw(model);
            var b = Raw(AiPack.ReadPitchModel(pack.RmvpeModel!));
            double corr = ImportTests.Corr(a, b), maxDiff = a.Zip(b, (p, q) => Math.Abs(p - q)).Max();
            output.WriteLine($"pitch model fp32 vs fp16-stored: correlation {corr:0.000000}, largest difference {maxDiff:0.00000}");
            Assert.True(corr > 0.9999, $"correlation {corr}");
            Assert.True(maxDiff < 0.01, $"largest difference {maxDiff}");
        }
        finally
        {
            Core.Storage.Cleanup.TryDelete(data);
        }
    }
}
