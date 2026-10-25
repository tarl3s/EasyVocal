using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyVocal.Ai.Store;

/// <summary>What a voice's files are for.</summary>
public static class VoiceKinds
{
    /// <summary>A speaker vector (<c>speaker.f32</c>, 256 floats) for a shared base generator in the pack (<see cref="VoiceManifest.Base"/>).</summary>
    public const string Speaker = "speaker";
    /// <summary>An RVC <c>.pth</c> (read with no PyTorch through a template).</summary>
    public const string Pth = "pth";
    /// <summary>EasyVocal's two graphs (<c>enc.onnx</c> + <c>dec.onnx</c>).</summary>
    public const string OnnxPair = "onnx-pair";
    /// <summary>One ONNX graph as RVC WebUI or w-okada export it.</summary>
    public const string OnnxSingle = "onnx-single";
}

/// <summary>One file of a voice and how to check it.</summary>
public sealed class VoiceFile
{
    /// <summary>"model", "enc", "dec", "speaker", "index".</summary>
    public string Role { get; set; } = "";
    /// <summary>Plain file name inside the voice's folder.</summary>
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>
/// <c>voice.json</c>: one AI voice in the pack's <c>voices/&lt;id&gt;/</c> folder. Ids are persisted (VoicePreset.AiVoiceId):
/// never rename one.
/// </summary>
public sealed class VoiceManifest
{
    public const string FileName = "voice.json";
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Emoji { get; set; } = "🧠";
    /// <summary>One of <see cref="VoiceKinds"/>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>For <see cref="VoiceKinds.Speaker"/>: the pack's base generator it uses ("rvc-v2-40k").</summary>
    public string? Base { get; set; }
    /// <summary>The model's output rate (32000, 40000 or 48000).</summary>
    public int SampleRate { get; set; } = 40000;
    /// <summary>"v2".</summary>
    public string Version { get; set; } = "v2";
    /// <summary>The voice's natural pitch in Hz: the input is moved there (null = only <see cref="Key"/> applies).</summary>
    public double? TargetHz { get; set; }
    /// <summary>Semitones on top (or the whole shift without <see cref="TargetHz"/>).</summary>
    public double Key { get; set; }
    /// <summary>Shipped with EasyVocal (removing the pack removes it; it comes back with the pack).</summary>
    public bool BuiltIn { get; set; }
    /// <summary>A short description shown with the voice.</summary>
    public string? Description { get; set; }
    /// <summary>Where it came from (a URL, "imported file", the dataset).</summary>
    public string? Source { get; set; }
    public string? Licence { get; set; }
    public string? Attribution { get; set; }
    public DateTime? AddedUtc { get; set; }
    public List<VoiceFile> Files { get; set; } = new();

    [JsonIgnore]
    public VoiceFile? Index => Files.FirstOrDefault(f => f.Role == "index");

    public VoiceFile? File(string role) => Files.FirstOrDefault(f => f.Role == role);

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static VoiceManifest Read(string path)
    {
        var m = JsonSerializer.Deserialize<VoiceManifest>(System.IO.File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Empty voice.json.");
        if (m.Format > CurrentFormat) throw new InvalidDataException("This voice was made by a newer EasyVocal.");
        if (string.IsNullOrWhiteSpace(m.Id)) throw new InvalidDataException("A voice.json without an id.");
        foreach (var f in m.Files)
            if (!Core.Storage.FileNames.IsPlain(f.Name)) throw new InvalidDataException("A voice.json names a file outside its folder.");
        return m;
    }

    public void Write(string path)
    {
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), new System.Text.UTF8Encoding(false));
        System.IO.File.Move(tmp, path, overwrite: true);
    }
}
