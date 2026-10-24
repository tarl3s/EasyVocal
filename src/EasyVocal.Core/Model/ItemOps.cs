using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using EasyVocal.Core.Text;

namespace EasyVocal.Core.Model;

/// <summary>
/// What "Duplicate", "Delete" and "Add sound" do to the settings and the Sounds folder. Pure data and files: no
/// audio, no windows. The caller (the controller) saves the settings and tells the engine.
/// </summary>
public static class ItemOps
{
    private static readonly Regex TrailingNumber = new(@"^(.*\S)\s+(\d+)$", RegexOptions.Compiled);

    /// <summary>A short id for something the person made (never reused: ids are saved in settings and hotkeys).</summary>
    public static string NewId() => "my-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// <paramref name="wanted"/> if no name in <paramref name="existing"/> is the same (ignoring case), else the
    /// same name with the next free number: "My filter 2", "My filter 3"…
    /// </summary>
    public static string UniqueName(IEnumerable<string> existing, string wanted)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(wanted)) return wanted;
        var stem = wanted;
        int n = 2;
        var m = TrailingNumber.Match(wanted);
        if (m.Success && int.TryParse(m.Groups[2].Value, out int given) && given < 1000000)
        {
            stem = m.Groups[1].Value;
            n = given + 1;
        }
        while (taken.Contains(stem + " " + n)) n++;
        return stem + " " + n;
    }

    /// <summary>The wanted name of a copy: "Monster copy" for a built-in (shown translated), the same name for the person's own (made unique: "My filter 2").</summary>
    private static string CopyName(string name, bool builtIn) =>
        builtIn ? L.F("{0} copy", L.T(name)) : name; // a taken name gets the next number

    // ---------- voices ----------

    /// <summary>A custom copy right after the original: own id, no hotkey, sliders of its own.</summary>
    public static VoicePreset DuplicateVoice(AppSettings s, string id)
    {
        int index = s.Voices.FindIndex(v => v.Id == id);
        if (index < 0) throw new ArgumentException("No voice with id '" + id + "'.", nameof(id));
        var original = s.Voices[index];
        var copy = new VoicePreset
        {
            Id = NewId(),
            Name = UniqueName(s.Voices.ConvertAll(v => v.Name), CopyName(original.Name, original.BuiltIn)),
            Emoji = original.Emoji,
            BuiltIn = false,
            Hidden = false,
            Hotkey = null,
            Kind = original.Kind,
            Sliders = original.Sliders.Clone(),
        };
        s.Voices.Insert(index + 1, copy);
        return copy;
    }

    /// <summary>False only for Normal: the clean voice must always be there to pick (and to fall back to).</summary>
    public static bool CanHide(VoicePreset v) => v.Id != BuiltInVoices.NormalId;

    /// <summary>
    /// A built-in voice is hidden (it can come back); the person's own is removed. The active voice falls back to
    /// Normal. Normal itself is never hidden.
    /// </summary>
    public static void DeleteVoice(AppSettings s, string id)
    {
        int index = s.Voices.FindIndex(v => v.Id == id);
        if (index < 0 || !CanHide(s.Voices[index])) return;
        if (s.Voices[index].BuiltIn) s.Voices[index].Hidden = true;
        else s.Voices.RemoveAt(index);
        if (s.ActiveVoiceId == id) s.ActiveVoiceId = BuiltInVoices.NormalId;
    }

    // ---------- sounds ----------

    /// <summary>A custom copy right after the original. It plays the same file (the file is kept while anything uses it).</summary>
    public static SoundItem DuplicateSound(AppSettings s, string id)
    {
        int index = s.Sounds.FindIndex(x => x.Id == id);
        if (index < 0) throw new ArgumentException("No sound with id '" + id + "'.", nameof(id));
        var original = s.Sounds[index];
        var copy = new SoundItem
        {
            Id = NewId(),
            Name = UniqueName(s.Sounds.ConvertAll(x => x.Name), CopyName(original.Name, original.BuiltIn)),
            Emoji = original.Emoji,
            FileName = original.FileName,
            Volume = original.Volume,
            Hotkey = null,
            BuiltIn = false,
            Hidden = false,
        };
        s.Sounds.Insert(index + 1, copy);
        return copy;
    }

    /// <summary>
    /// A built-in sound is hidden; the person's own is removed, and so is its file in <paramref name="soundsFolder"/>
    /// when no other sound uses it (a copy shares its original's file).
    /// </summary>
    public static void DeleteSound(AppSettings s, string id, string soundsFolder)
    {
        int index = s.Sounds.FindIndex(x => x.Id == id);
        if (index < 0) return;
        var item = s.Sounds[index];
        if (item.BuiltIn)
        {
            item.Hidden = true;
            return;
        }
        s.Sounds.RemoveAt(index);
        DeleteFileIfUnused(s, item.FileName, soundsFolder);
    }

    /// <summary>
    /// Deletes <paramref name="fileName"/> from <paramref name="soundsFolder"/> when no sound in the settings uses it.
    /// Anything that isn't a plain file name (a path out of the folder) is left alone.
    /// </summary>
    public static void DeleteFileIfUnused(AppSettings s, string fileName, string soundsFolder)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName) return; // never leave the folder
        if (s.Sounds.Exists(x => string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            var path = Path.Combine(soundsFolder, fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* in use or gone: a leftover file is harmless */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Copies a sound file into <paramref name="soundsFolder"/> (created if missing) under a name nobody uses:
    /// "bruh.mp3", then "bruh (2).mp3", "bruh (3).mp3"… Returns the plain file name to store in <see cref="SoundItem.FileName"/>.
    /// A file that is already in the folder is not copied again.
    /// </summary>
    public static string ImportSoundFile(string sourcePath, string soundsFolder)
    {
        Directory.CreateDirectory(soundsFolder);
        var source = Path.GetFullPath(sourcePath);
        var name = Path.GetFileName(source);
        var folder = Path.GetFullPath(soundsFolder);
        if (string.Equals(Path.GetDirectoryName(source), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int n = 1; ; n++)
        {
            var candidate = n == 1 ? name : $"{stem} ({n}){ext}";
            try
            {
                File.Copy(source, Path.Combine(folder, candidate), overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(Path.Combine(folder, candidate)))
            {
                // taken: try the next number
            }
        }
    }
}
