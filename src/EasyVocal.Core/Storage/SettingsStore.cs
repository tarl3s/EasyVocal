using System.Text.Encodings.Web;
using System.Text.Json;
using EasyVocal.Core.Model;

namespace EasyVocal.Core.Storage;

public static class SettingsStore
{
    private const string FileNameJson = "settings.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyVocal");

    public static AppSettings Load(string folder)
    {
        var path = Path.Combine(folder, FileNameJson);
        AppSettings? s = null;
        if (File.Exists(path))
        {
            try
            {
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
            }
            catch (Exception)
            {
                s = null;
            }
            if (s == null) KeepBadCopy(path);
        }
        return Normalize(s ?? new AppSettings());
    }

    public static void Save(string folder, AppSettings s)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileNameJson);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private static void KeepBadCopy(string path)
    {
        try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
    }

    private static AppSettings Normalize(AppSettings s)
    {
        s.GeneralHotkeys ??= new();
        s.Voices = MergeVoices(s.Voices ?? new());
        s.Sounds = MergeSounds(s.Sounds ?? new());
        return s;
    }

    // Built-ins: user-owned fields (Hidden, Hotkey) from the file, everything else from code.
    // Missing built-ins are appended in catalog order, then user items in file order.
    private static List<VoicePreset> MergeVoices(List<VoicePreset> fromFile)
    {
        var result = new List<VoicePreset>();
        var catalogIds = new HashSet<string>();
        foreach (var b in BuiltInVoices.All())
        {
            catalogIds.Add(b.Id);
            var f = fromFile.FirstOrDefault(v => v != null && v.Id == b.Id);
            if (f != null) { b.Hidden = f.Hidden; b.Hotkey = f.Hotkey; }
            result.Add(b);
        }
        foreach (var v in fromFile)
        {
            if (v == null || string.IsNullOrEmpty(v.Id) || catalogIds.Contains(v.Id)) continue;
            v.BuiltIn = false;
            v.Sliders = (v.Sliders ?? new VoiceSliders()).Clamp();
            result.Add(v);
        }
        return result;
    }

    private static List<SoundItem> MergeSounds(List<SoundItem> fromFile)
    {
        var result = new List<SoundItem>();
        var catalogIds = new HashSet<string>();
        foreach (var b in StarterSounds.Items())
        {
            catalogIds.Add(b.Id);
            var f = fromFile.FirstOrDefault(x => x != null && x.Id == b.Id);
            if (f != null)
            {
                b.Hidden = f.Hidden;
                b.Hotkey = f.Hotkey;
                b.Volume = ClampVolume(f.Volume, b.Volume);
            }
            result.Add(b);
        }
        foreach (var x in fromFile)
        {
            if (x == null || string.IsNullOrEmpty(x.Id) || catalogIds.Contains(x.Id)) continue;
            x.BuiltIn = false;
            x.Volume = ClampVolume(x.Volume, 0.8);
            result.Add(x);
        }
        return result;
    }

    private static double ClampVolume(double v, double fallback) =>
        double.IsNaN(v) ? fallback : Math.Clamp(v, 0, 1);
}
