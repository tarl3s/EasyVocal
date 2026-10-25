using EasyVocal.Ai;
using EasyVocal.Ai.Import;
using EasyVocal.Ai.Store;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Ai.Tests.Helpers;

/// <summary>The tiny committed fixtures (tools/ai/export/make_fixtures.py) and a tiny AI pack made of them.</summary>
public static class AiFixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string P(string name) => Path.Combine(Dir, name);

    public const string VoiceA = "tiny-a", VoiceB = "tiny-b";

    /// <summary>
    /// A new data folder (%TEMP%\EV_AiTiny_*) whose AI pack is made of stand-ins with the real interfaces: a content
    /// encoder with HuBERT's frame count, a pitch model with RMVPE's shapes, the tiny RVC v2 40k network as the base
    /// generator (with its templates in engine/templates) and two voices on its two speakers. Fast on any CI runner.
    /// </summary>
    public static string TinyDataFolder()
    {
        var data = TestFolders.New("EV_AiTiny_");
        var pack = new AiPack(data);
        Directory.CreateDirectory(Path.Combine(pack.EngineFolder, "templates"));
        File.Copy(P("tiny_hubert.onnx"), Path.Combine(pack.EngineFolder, AiPack.ContentFp32));
        File.Copy(P("tiny_rmvpe.onnx"), Path.Combine(pack.EngineFolder, AiPack.Rmvpe));
        File.Copy(P("tiny.pth"), Path.Combine(pack.EngineFolder, BaseModels.BaseGeneratorId + ".pth"));
        File.Copy(P("tiny_enc.onnx.gz"), Path.Combine(pack.EngineFolder, "templates", "rvc_v2_f0_40k_enc.onnx.gz"));
        File.Copy(P("tiny_dec.onnx.gz"), Path.Combine(pack.EngineFolder, "templates", "rvc_v2_f0_40k_dec.onnx.gz"));
        var emb = TorchCheckpoint.Load(P("tiny.pth")).Tensors["emb_g.weight"];
        WriteSpeakerVoice(pack, VoiceA, "Tiny A", RvcTemplates.SpeakerVector(emb, 0), 220);
        WriteSpeakerVoice(pack, VoiceB, "Tiny B", RvcTemplates.SpeakerVector(emb, 1), null);
        return data;
    }

    public static VoiceManifest WriteSpeakerVoice(AiPack pack, string id, string name, float[] speaker, double? targetHz)
    {
        var dir = pack.VoiceFolder(id);
        Directory.CreateDirectory(dir);
        var spk = Path.Combine(dir, "speaker.f32");
        AiVoiceService.WriteSpeaker(spk, speaker);
        var m = new VoiceManifest
        {
            Id = id, Name = name, Kind = VoiceKinds.Speaker, Base = BaseModels.BaseGeneratorId, SampleRate = 40000, TargetHz = targetHz,
            BuiltIn = true, Licence = "test", Files = { new VoiceFile { Role = "speaker", Name = "speaker.f32", Size = 1024, Sha256 = Downloader.Sha256Of(spk) } },
        };
        m.Write(Path.Combine(dir, VoiceManifest.FileName));
        return m;
    }
}
