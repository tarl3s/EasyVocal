using System.Text;
using EasyVocal.Ai.Rvc;
using EasyVocal.Ai.Runtime;
using EasyVocal.Ai.Store;
using EasyVocal.Ai.Tests.Helpers;
using EasyVocal.Tests.Helpers;
using Xunit.Abstractions;

namespace EasyVocal.Ai.Tests;

/// <summary>
/// The bundled voices on the real models (RVC's MIT files; see <see cref="RealModels"/>) and real speech (Windows TTS;
/// a synthetic vowel elsewhere): each voice keeps the speaker's melody, sounds like itself whoever speaks, and further
/// from the speaker than the input was.
/// </summary>
[Collection("Timing")]
public class RealModelTests
{
    private readonly ITestOutputHelper output;

    public RealModelTests(ITestOutputHelper output) => this.output = output;

    private AiVoiceService? Service()
    {
        var data = RealModels.DataFolder(out var skip);
        if (data == null)
        {
            output.WriteLine("SKIPPED: " + skip);
            return null;
        }
        return new AiVoiceService(data);
    }

    [Fact]
    public void The_pack_built_from_the_public_files_has_every_part_and_the_bundled_voices()
    {
        using var s = Service();
        if (s == null) return;
        Assert.True(s.Available, s.UnavailableReason);
        Assert.NotNull(s.Pack.RmvpeModel);
        Assert.NotNull(s.Pack.BaseGenerator(BaseModels.BaseGeneratorId));
        var voices = s.Voices.All();
        var recipe = BundledRecipe.Embedded();
        Assert.Equal(recipe.Voices.Select(v => v.Id).OrderBy(x => x), voices.Where(v => v.BuiltIn).Select(v => v.Id).OrderBy(x => x));
        foreach (var v in voices) Assert.Null(s.Voices.Check(v));
        Assert.All(voices, v => Assert.Contains("VCTK", v.Attribution));
    }

    [Fact]
    public void Bundled_voices_follow_the_melody_and_each_sounds_like_itself()
    {
        using var s = Service();
        if (s == null) return;
        var speech = Speech.Signal;
        output.WriteLine("input: " + Speech.Source);
        // two different parts of the speech (other words, and on Windows other speakers)
        int half = speech.Length / 2;
        while (half < speech.Length - 4800 && Math.Abs(speech[half]) > 1e-3) half++;
        var a = speech.AsSpan(0, half).ToArray();
        var b = speech.AsSpan(half).ToArray();
        var ids = s.Voices.All().Where(v => v.BuiltIn).Select(v => v.Id).ToList();
        var outA = new Dictionary<string, float[]>();
        var outB = new Dictionary<string, float[]>();
        foreach (var id in ids)
        {
            using var v = s.Load(id, AiQuality.Balanced, AiDevice.Cpu);
            outA[id] = RvcOffline.Convert(v.Voice, a, indexRate: 0);
            outB[id] = RvcOffline.Convert(v.Voice, b, indexRate: 0);
        }
        if (Environment.GetEnvironmentVariable("EV_AI_SAMPLES") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            WavFile.WriteMono(Path.Combine(dir, "input_a.wav"), a);
            WavFile.WriteMono(Path.Combine(dir, "input_b.wav"), b);
            foreach (var id in ids) { WavFile.WriteMono(Path.Combine(dir, id + "_a.wav"), outA[id]); WavFile.WriteMono(Path.Combine(dir, id + "_b.wav"), outB[id]); }
        }
        var printA = ids.ToDictionary(id => id, id => VoiceMeasure.Timbre(outA[id]));
        var refs = ids.ToDictionary(id => id, id => VoiceMeasure.Timbre(outB[id]));
        var inA = VoiceMeasure.Timbre(a);
        int right = 0;
        var table = new StringBuilder();
        table.AppendLine("voice                    | shift st | spread c | voiced | d(own) | d(input,own) | nearest");
        bool synthetic = Speech.Source == "synthetic";
        foreach (var id in ids)
        {
            var (st, spread, voiced) = VoiceMeasure.PitchFollow(a, outA[id]);
            double own = VoiceMeasure.Distance(printA[id], refs[id]);
            double fromInput = VoiceMeasure.Distance(inA, refs[id]);
            var nearest = ids.OrderBy(o => VoiceMeasure.Distance(printA[id], refs[o])).First();
            if (nearest == id) right++;
            table.AppendLine($"{id,-24} | {st,8:0.0} | {spread,8:0} | {voiced,6:0.00} | {own,6:0.0} | {fromInput,12:0.0} | {nearest}");
            Assert.True(spread < 80, $"{id}: the melody is followed (spread {spread:0} cents)");
            Assert.True(voiced > 0.5, $"{id}: voiced where the input is ({voiced:0.00})");
            Assert.True(!synthetic && own < fromInput || synthetic, $"{id}: closer to itself ({own:0.0}) than the input was ({fromInput:0.0})");
        }
        output.WriteLine(table.ToString());
        output.WriteLine($"identified {right}/{ids.Count}");
        if (!synthetic) Assert.True(right >= ids.Count - 2, $"only {right}/{ids.Count} voices recognisable as themselves");
        // pitch: women up, men down, from the same speaker
        var shift = ids.ToDictionary(id => id, id => VoiceMeasure.PitchFollow(a, outA[id]).Semitones);
        Assert.True(shift["ai-bright-young-woman"] > shift["ai-deep-calm-man"] + 8, "the woman's voice is well above the deep man's");
    }

    [Fact]
    public void The_content_encoder_built_from_public_weights_matches_the_int8_one()
    {
        using var s = Service();
        if (s == null) return;
        var fp32 = s.Pack.ContentModel(preferInt8: false)!;
        var int8 = s.Pack.ContentModel(preferInt8: true)!;
        var x = Speech.Signal.AsSpan(0, 48000 * 3).ToArray();
        var x16 = EasyVocal.Ai.Dsp.PolyResampler.Convert(x, 48000, 16000);
        float[] Run(string path)
        {
            using var m = OrtRuntime.CreateFromFile("content", path, AiDevice.Cpu, 4);
            using var e = new ContentEncoder(m);
            var o = new float[x16.Length / 160 * 768];
            e.Run(x16, o);
            return o;
        }
        // the same model exported with its weights by PyTorch (EV_AI_REF_HUBERT, e.g. the phase 1 spike's hubert_base.onnx)
        if (Environment.GetEnvironmentVariable("EV_AI_REF_HUBERT") is { Length: > 0 } refPath && File.Exists(refPath))
        {
            double r = ImportTests.Corr(Run(fp32), Run(refPath));
            output.WriteLine($"fp32 built here vs PyTorch's own export: correlation {r:0.000000}");
            Assert.True(r > 0.9999, $"correlation {r}");
        }
        if (fp32 == int8) { output.WriteLine("SKIPPED: no int8 content encoder in this pack (only the fp32 one)"); return; }
        double c = ImportTests.Corr(Run(fp32), Run(int8));
        output.WriteLine($"fp32 (built here from the state dict) vs int8 (exported with PyTorch): correlation {c:0.0000}");
        Assert.True(c > 0.98, $"correlation {c}");
    }

    [Fact]
    public async Task A_real_pth_imports_without_PyTorch_and_sounds_like_the_same_speaker_on_the_base_model()
    {
        using var s = Service();
        if (s == null) return;
        var importer = new VoiceImporter(s.Pack, s.Voices);
        var basePth = s.Pack.BaseGenerator(BaseModels.BaseGeneratorId)!;
        VoiceManifest? imported = null;
        const string sameId = "test-base-speaker-0";
        try
        {
            imported = await importer.ImportFilesAsync(new[] { basePth }, "Base model", source: "test");
            Assert.Equal(VoiceKinds.Pth, imported.Kind);
            Assert.Equal(40000, imported.SampleRate);
            // the same speaker as a speaker vector on the shared base generator
            var emb = EasyVocal.Ai.Import.TorchCheckpoint.Load(basePth).Tensors["emb_g.weight"];
            AiFixtures.WriteSpeakerVoice(s.Pack, sameId, "Speaker 0", EasyVocal.Ai.Import.RvcTemplates.SpeakerVector(emb, 0), null);
            var x = Speech.Signal.AsSpan(0, 48000 * 3).ToArray();
            float[] a, b;
            using (var v = s.Load(imported.Id, AiQuality.Balanced, AiDevice.Cpu)) a = RvcOffline.Convert(v.Voice, x, indexRate: 0);
            using (var v = s.Load(sameId, AiQuality.Balanced, AiDevice.Cpu)) b = RvcOffline.Convert(v.Voice, x, indexRate: 0);
            double c = ImportTests.Corr(a, b);
            output.WriteLine($"imported .pth vs the pack's base model + speaker 0: correlation {c:0.0000}");
            Assert.True(c > 0.99, $"correlation {c}");
        }
        finally
        {
            if (imported != null) s.Voices.Delete(imported.Id);
            s.Voices.Delete(sameId);
        }
    }

    [Fact]
    public async Task A_community_voice_imports_and_sounds_like_its_own_sample()
    {
        // a real community .pth/.index (EV_AI_TESTMODEL = a folder with default.pth, default.index, sample.wav; the
        // phase 1 spike used PhoenixStormJr/RVC-V2-default-voice, MIT): only when given, it isn't ours to ship
        var dir = Environment.GetEnvironmentVariable("EV_AI_TESTMODEL");
        using var s = Service();
        if (s == null) return;
        if (dir == null || !File.Exists(Path.Combine(dir, "default.pth"))) { output.WriteLine("SKIPPED: EV_AI_TESTMODEL isn't set"); return; }
        var importer = new VoiceImporter(s.Pack, s.Voices);
        var files = new List<string> { Path.Combine(dir, "default.pth") };
        if (File.Exists(Path.Combine(dir, "default.index"))) files.Add(Path.Combine(dir, "default.index"));
        var m = await importer.ImportFilesAsync(files, "Community voice");
        try
        {
            var sample = WavFile.ReadMono(Path.Combine(dir, "sample.wav"));
            var x = Speech.Signal;
            float[] y;
            using (var v = s.Load(m.Id, AiQuality.Max, AiDevice.Cpu)) y = RvcOffline.Convert(v.Voice, x);
            var own = VoiceMeasure.Timbre(sample);
            double dOut = VoiceMeasure.Distance(VoiceMeasure.Timbre(y), own), dIn = VoiceMeasure.Distance(VoiceMeasure.Timbre(x), own);
            output.WriteLine($"timbre distance to the voice's own sample: converted {dOut:0.0}, input {dIn:0.0}");
            Assert.True(dOut < dIn);
        }
        finally { s.Voices.Delete(m.Id); }
    }
}
