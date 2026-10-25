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

    /// <summary>
    /// Windows: %APPDATA%\EasyVocal; macOS: ~/Library/Application Support/EasyVocal; Linux: ~/.config/EasyVocal
    /// ($XDG_CONFIG_HOME when set).
    /// </summary>
    public static string DefaultFolder => DefaultFolderFor(
        OperatingSystem.IsMacOS(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>The data folder for a system (testable on any OS).</summary>
    public static string DefaultFolderFor(bool macOS, string home, string applicationData) => macOS
        ? Path.Combine(home, "Library", "Application Support", "EasyVocal")
        : Path.Combine(applicationData, "EasyVocal");

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
            if (s == null && KeepBadCopy(path)) Cleanup.TryDelete(path);
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
    // sound's name and emoji are labels the person may change in the sound editor, so they are theirs too. The file's
    // order is the person's (cards can be dragged); a built-in missing from it (new in this version) goes right after
    // the built-in before it in the catalog.
    private static List<VoicePreset> MergeVoices(List<VoicePreset> fromFile) =>
        InFileOrder(fromFile, BuiltInVoices.All(), v => v.Id, (b, f) =>
        {
            b.Hidden = f.Hidden && ItemOps.CanHide(b);
            b.Hotkey = f.Hotkey;
        }, v =>
        {
            v.BuiltIn = false;
            v.Sliders = (v.Sliders ?? new VoiceSliders()).Clamp();
        });

    private static List<SoundItem> MergeSounds(List<SoundItem> fromFile) =>
        InFileOrder(fromFile, StarterSounds.Items(), x => x.Id, (b, f) =>
        {
            b.Hidden = f.Hidden;
            b.Hotkey = f.Hotkey;
            b.Volume = ClampVolume(f.Volume, b.Volume);
            if (!string.IsNullOrWhiteSpace(f.Name)) b.Name = f.Name;
            if (!string.IsNullOrWhiteSpace(f.Emoji)) b.Emoji = f.Emoji;
            // Edited in the sound editor: it plays the edited copy; its own file stays the original.
            if (!string.IsNullOrEmpty(f.OriginalFileName) && FileNames.IsPlain(f.FileName) && !PathCompare.Same(f.FileName, b.FileName))
            {
                b.OriginalFileName = b.FileName;
                b.FileName = f.FileName;
            }
        }, x =>
        {
            x.BuiltIn = false;
            x.Volume = ClampVolume(x.Volume, 0.8);
            if (x.OriginalFileName != null && (!FileNames.IsPlain(x.OriginalFileName) || PathCompare.Same(x.OriginalFileName, x.FileName)))
                x.OriginalFileName = null;
        });

    /// <param name="catalog">The built-ins as the code makes them (fresh objects).</param>
    /// <param name="fromBuiltIn">Copies the person's fields of a built-in from the file's entry.</param>
    /// <param name="fixUser">Makes an item of the person's sane.</param>
    private static List<T> InFileOrder<T>(List<T> fromFile, IEnumerable<T> catalog, Func<T, string?> idOf, Action<T, T> fromBuiltIn, Action<T> fixUser)
        where T : class
    {
        var builtIns = new Dictionary<string, T>();
        var order = new List<string>();
        foreach (var b in catalog)
        {
            builtIns[idOf(b)!] = b;
            order.Add(idOf(b)!);
        }
        var result = new List<T>();
        var placed = new HashSet<string>();
        foreach (var f in fromFile)
        {
            if (f == null || idOf(f) is not { Length: > 0 } id) continue;
            if (builtIns.TryGetValue(id, out var b))
            {
                if (!placed.Add(id)) continue; // a built-in twice: the first one counts
                fromBuiltIn(b, f);
                result.Add(b);
            }
            else
            {
                fixUser(f);
                result.Add(f);
            }
        }
        for (int i = 0; i < order.Count; i++)
        {
            if (placed.Contains(order[i])) continue;
            int at = 0; // after the nearest built-in before it in the catalog that is there (else first)
            for (int k = i - 1; k >= 0; k--)
            {
                int found = result.FindIndex(x => idOf(x) == order[k]);
                if (found >= 0) { at = found + 1; break; }
            }
            result.Insert(at, builtIns[order[i]]);
            placed.Add(order[i]);
        }
        return result;
    }

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
