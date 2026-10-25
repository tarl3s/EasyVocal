using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EasyVocal.Core.Model;

namespace EasyVocal.Core.Storage;

/// <summary>
/// One data folder's settings.json for the life of the app. <see cref="Load"/> reads it (retrying a moment when
/// another program holds it); when the file exists but still can't be read, the session is read-only:
/// <see cref="Save"/> then writes nothing, so the person's real settings are never replaced by defaults.
/// </summary>
public sealed class SettingsFile
{
    private readonly string folder;

    public SettingsFile(string folder) => this.folder = folder;

    /// <summary>settings.json existed but could not be read (locked, no permission): nothing is saved this session.</summary>
    public bool CouldNotRead { get; private set; }

    public AppSettings Load()
    {
        var s = SettingsStore.Load(folder, out bool couldNotRead);
        CouldNotRead = couldNotRead;
        return s;
    }

    /// <summary>Writes the settings; false (nothing written) while <see cref="CouldNotRead"/>.</summary>
    public bool Save(AppSettings s)
    {
        if (CouldNotRead) return false;
        SettingsStore.Save(folder, s);
        return true;
    }
}

public static class SettingsStore
{
    private const string FileNameJson = "settings.json";
    private const int ReadAttempts = 3;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(100);

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

    public static AppSettings Load(string folder) => Load(folder, out _);

    /// <summary>
    /// Reads settings.json. A damaged file is moved to settings.json.bad and the previous good copy
    /// (settings.json.bak) is used instead; with neither, defaults. <paramref name="couldNotRead"/> is true when
    /// settings.json exists but could not be read at all (another program holds it, no permission): the caller
    /// must not save over it this session.
    /// </summary>
    public static AppSettings Load(string folder, out bool couldNotRead)
    {
        var path = Path.Combine(folder, FileNameJson);
        var backup = path + ".bak";
        couldNotRead = false;
        AppSettings? s = null;
        var text = ReadWithRetry(path, out bool unreadable);
        if (unreadable)
        {
            couldNotRead = true;
        }
        else if (text != null)
        {
            s = Parse(text);
            // The damaged file is kept as .bad and moved out of the way: the next Save would otherwise put it in
            // .bak, over the good backup this load is about to use.
            if (s == null && KeepBadCopy(path)) TryDelete(path);
        }
        if (s == null && File.Exists(backup))
        {
            var old = ReadWithRetry(backup, out _);
            if (old != null) s = Parse(old);
        }
        return Normalize(s ?? new AppSettings());
    }

    /// <summary>
    /// Power-loss safe: the new settings go to settings.json.tmp, are flushed to the disk, then replace settings.json
    /// in one step; the previous file stays as settings.json.bak.
    /// </summary>
    public static void Save(string folder, AppSettings s)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileNameJson);
        var tmp = path + ".tmp";
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(s, Json));
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }
        try
        {
            File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            // A file system without an atomic replace: keep the old copy, then move the new file over.
            File.Copy(path, path + ".bak", overwrite: true);
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>The file's text; null when there is no file. <paramref name="unreadable"/>: it exists but every try failed.</summary>
    private static string? ReadWithRetry(string path, out bool unreadable)
    {
        unreadable = false;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null; // removed while we looked
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts)
                {
                    unreadable = true;
                    return null;
                }
                Thread.Sleep(ReadRetryDelay);
            }
        }
    }

    private static AppSettings? Parse(string text)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(text, Json); }
        catch (Exception) { return null; }
    }

    /// <summary>True when the damaged file is now safe in settings.json.bad.</summary>
    private static bool KeepBadCopy(string path)
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* left in place: the worst case is the old behaviour */ }
    }

    private static AppSettings Normalize(AppSettings s)
    {
        s.GeneralHotkeys ??= new();
        s.WindowPlacements = KeepSaneBoxes(s.WindowPlacements);
        s.Voices = MergeVoices(s.Voices ?? new());
        s.Sounds = MergeSounds(s.Sounds ?? new());
        return s;
    }

    // Built-ins: user-owned fields from the file, everything else from code. For voices that is Hidden and Hotkey
    // (editing a built-in voice saves a copy; Normal is never hidden, even by a hand-edited file); a built-in
    // sound's name and emoji are labels the person may change in the sound editor, so they are theirs too. Missing
    // built-ins are appended in catalog order, then user items in file order.
    private static List<VoicePreset> MergeVoices(List<VoicePreset> fromFile)
    {
        var result = new List<VoicePreset>();
        var catalogIds = new HashSet<string>();
        foreach (var b in BuiltInVoices.All())
        {
            catalogIds.Add(b.Id);
            var f = fromFile.FirstOrDefault(v => v != null && v.Id == b.Id);
            if (f != null) { b.Hidden = f.Hidden && ItemOps.CanHide(b); b.Hotkey = f.Hotkey; }
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
                if (!string.IsNullOrWhiteSpace(f.Name)) b.Name = f.Name;
                if (!string.IsNullOrWhiteSpace(f.Emoji)) b.Emoji = f.Emoji;
                // Edited in the sound editor: it plays the edited copy; its own file stays the original.
                if (!string.IsNullOrEmpty(f.OriginalFileName) && IsPlainName(f.FileName) && !string.Equals(f.FileName, b.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    b.OriginalFileName = b.FileName;
                    b.FileName = f.FileName;
                }
            }
            result.Add(b);
        }
        foreach (var x in fromFile)
        {
            if (x == null || string.IsNullOrEmpty(x.Id) || catalogIds.Contains(x.Id)) continue;
            x.BuiltIn = false;
            x.Volume = ClampVolume(x.Volume, 0.8);
            if (x.OriginalFileName != null && (!IsPlainName(x.OriginalFileName) || string.Equals(x.OriginalFileName, x.FileName, StringComparison.OrdinalIgnoreCase)))
                x.OriginalFileName = null;
            result.Add(x);
        }
        return result;
    }

    /// <summary>A file name of the Sounds folder itself (no folders, nothing that leaves it).</summary>
    private static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Path.GetFileName(name) == name && name != "." && name != "..";

    private static Dictionary<string, WindowBox> KeepSaneBoxes(Dictionary<string, WindowBox>? boxes)
    {
        var result = new Dictionary<string, WindowBox>();
        if (boxes == null) return result;
        foreach (var (key, box) in boxes)
            if (!string.IsNullOrEmpty(key) && box.IsFinite && box.Width < 100000 && box.Height < 100000) result[key] = box;
        return result;
    }

    private static double ClampVolume(double v, double fallback) =>
        double.IsNaN(v) ? fallback : Math.Clamp(v, 0, 1);
}
