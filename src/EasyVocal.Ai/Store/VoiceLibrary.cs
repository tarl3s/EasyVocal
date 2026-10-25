using EasyVocal.Core.Logging;

namespace EasyVocal.Ai.Store;

/// <summary>The voices in the pack's <c>voices/</c> folder: list, find, check, delete.</summary>
public sealed class VoiceLibrary
{
    private readonly AiPack pack;

    public VoiceLibrary(AiPack pack) => this.pack = pack;

    /// <summary>Every readable voice, built-in ones first, then by name. A damaged voice.json is skipped (and logged).</summary>
    public IReadOnlyList<VoiceManifest> All()
    {
        var list = new List<VoiceManifest>();
        if (!Directory.Exists(pack.VoicesFolder)) return list;
        foreach (var dir in Directory.EnumerateDirectories(pack.VoicesFolder))
        {
            var file = Path.Combine(dir, VoiceManifest.FileName);
            if (!File.Exists(file)) continue;
            try
            {
                var m = VoiceManifest.Read(file);
                if (!string.Equals(m.Id, Path.GetFileName(dir), StringComparison.Ordinal)) continue; // folder and id must agree
                list.Add(m);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                AppLog.Info($"AI: skipping the voice in {Path.GetFileName(dir)} ({ex.Message})");
            }
        }
        return list.OrderByDescending(v => v.BuiltIn).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public VoiceManifest? Find(string id)
    {
        if (!Core.Storage.FileNames.IsPlain(id)) return null;
        var file = Path.Combine(pack.VoiceFolder(id), VoiceManifest.FileName);
        if (!File.Exists(file)) return null;
        try
        {
            var m = VoiceManifest.Read(file);
            return m.Id == id ? m : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The path of one of a voice's files.</summary>
    public string PathOf(VoiceManifest m, VoiceFile f) => Path.Combine(pack.VoiceFolder(m.Id), f.Name);

    /// <summary>Every file is there with its size and SHA-256; else the first problem.</summary>
    public string? Check(VoiceManifest m)
    {
        foreach (var f in m.Files)
        {
            var p = PathOf(m, f);
            if (!File.Exists(p)) return $"{f.Name} is missing";
            if (new FileInfo(p).Length != f.Size) return $"{f.Name} has the wrong size";
            if (!string.Equals(Downloader.Sha256Of(p), f.Sha256, StringComparison.OrdinalIgnoreCase)) return $"{f.Name} is damaged";
        }
        return null;
    }

    /// <summary>Removes a voice's folder. True when it is gone.</summary>
    public bool Delete(string id)
    {
        if (!Core.Storage.FileNames.IsPlain(id)) return false;
        var dir = pack.VoiceFolder(id);
        if (!Directory.Exists(dir)) return true;
        Core.Storage.Cleanup.TryDelete(dir);
        return !Directory.Exists(dir);
    }

    /// <summary>A new folder id from a name ("Deep calm man" → "deep-calm-man", "-2" when taken).</summary>
    public string NewId(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var id = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (id.Length == 0) id = "voice";
        if (id.Length > 40) id = id[..40].TrimEnd('-');
        id = "user-" + id;
        string candidate = id;
        for (int i = 2; Directory.Exists(pack.VoiceFolder(candidate)); i++) candidate = id + "-" + i;
        return candidate;
    }
}
