using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using EasyVocal.Core.Sounds;
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
            OriginalFileName = original.OriginalFileName,
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
        if (item.OriginalFileName != null) DeleteFileIfUnused(s, item.OriginalFileName, soundsFolder);
    }

    /// <summary>
    /// Deletes <paramref name="fileName"/> from <paramref name="soundsFolder"/> when no sound in the settings uses it
    /// (as its file or as the original of an edited one).
    /// Anything that isn't a plain file name (a path out of the folder) is left alone.
    /// </summary>
    public static void DeleteFileIfUnused(AppSettings s, string fileName, string soundsFolder)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName) return; // never leave the folder
        if (IsFileUsed(s, fileName)) return;
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

    /// <summary>True when a sound uses the file, as its file or as the original of an edited one.</summary>
    public static bool IsFileUsed(AppSettings s, string fileName) =>
        s.Sounds.Exists(x => string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(x.OriginalFileName, fileName, StringComparison.OrdinalIgnoreCase));

    // ---------- the sound editor ----------

    /// <summary>
    /// Writes edited samples as a new WAV in <paramref name="soundsFolder"/> under a name nobody uses
    /// ("<paramref name="stem"/>.wav", "… (2).wav"…) and returns that plain file name. Nothing is ever overwritten.
    /// </summary>
    public static string WriteSoundFile(string soundsFolder, string stem, float[] samples)
    {
        Directory.CreateDirectory(soundsFolder);
        stem = SafeStem(stem);
        for (int n = 1; ; n++)
        {
            var candidate = n == 1 ? stem + ".wav" : $"{stem} ({n}).wav";
            var path = Path.Combine(soundsFolder, candidate);
            FileStream stream;
            try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { continue; } // taken: try the next number
            try
            {
                SoundWriter.WriteWav(stream, samples); // closes the stream
            }
            catch
            {
                stream.Dispose();
                try { File.Delete(path); } catch (Exception) { /* a half-written file is only clutter */ }
                throw;
            }
            return candidate;
        }
    }

    /// <summary>A file name stem made of the name's allowed characters (at most 60), or "sound".</summary>
    public static string SafeStem(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (name ?? "").Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        var stem = new string(chars).Trim().TrimEnd('.');
        if (stem.Length > 60) stem = stem[..60].TrimEnd();
        return stem.Length == 0 ? "sound" : stem;
    }

    /// <summary>The stem for an edited copy of the sound: its original file's name + " (edited)".</summary>
    public static string EditedStem(SoundItem item) =>
        Path.GetFileNameWithoutExtension(item.OriginalFileName ?? item.FileName) + " (edited)";

    /// <summary>
    /// The sound now plays <paramref name="newFile"/> (an edited copy already in the folder). The file it came from
    /// is remembered the first time (<see cref="SoundItem.OriginalFileName"/>) and never touched; an earlier edited
    /// copy is deleted when nothing else uses it.
    /// </summary>
    public static void UseEditedFile(AppSettings s, SoundItem item, string newFile, string soundsFolder)
    {
        var old = item.FileName;
        if (string.IsNullOrEmpty(item.OriginalFileName)) item.OriginalFileName = old;
        item.FileName = newFile;
        if (!string.Equals(old, item.OriginalFileName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(old, newFile, StringComparison.OrdinalIgnoreCase))
            DeleteFileIfUnused(s, old, soundsFolder);
    }

    /// <summary>"Back to original": the sound plays its original file again; the edited copy goes when nothing uses it. False when it was never edited.</summary>
    public static bool BackToOriginal(AppSettings s, SoundItem item, string soundsFolder)
    {
        if (string.IsNullOrEmpty(item.OriginalFileName)) return false;
        var edited = item.FileName;
        item.FileName = item.OriginalFileName;
        item.OriginalFileName = null;
        if (!string.Equals(edited, item.FileName, StringComparison.OrdinalIgnoreCase)) DeleteFileIfUnused(s, edited, soundsFolder);
        return true;
    }

    /// <summary>
    /// The two halves of a split as new sounds of the person's own, "<paramref name="name"/> 1" and "… 2", right
    /// after <paramref name="afterId"/> (or at the end). Same emoji and volume, no hotkey.
    /// </summary>
    public static (SoundItem First, SoundItem Second) AddSplitSounds(AppSettings s, string? afterId, string name,
        string emoji, double volume, string firstFile, string secondFile)
    {
        var names = s.Sounds.ConvertAll(x => x.Name);
        var first = new SoundItem { Id = NewId(), Name = UniqueName(names, name + " 1"), Emoji = emoji, FileName = firstFile, Volume = volume };
        names.Add(first.Name);
        var second = new SoundItem { Id = NewId(), Name = UniqueName(names, name + " 2"), Emoji = emoji, FileName = secondFile, Volume = volume };
        int index = afterId == null ? -1 : s.Sounds.FindIndex(x => x.Id == afterId);
        if (index < 0) index = s.Sounds.Count - 1;
        s.Sounds.Insert(index + 1, first);
        s.Sounds.Insert(index + 2, second);
        return (first, second);
    }
}
