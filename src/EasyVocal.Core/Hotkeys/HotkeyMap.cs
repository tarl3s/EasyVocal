using EasyVocal.Core.Model;

namespace EasyVocal.Core.Hotkeys;

public static class HotkeyMap
{
    // The general actions' ids are keys of AppSettings.GeneralHotkeys: persisted, never rename.
    public const string ToggleFilter = "toggleFilter";
    public const string StopSounds = "stopSounds";
    public const string MuteMic = "muteMic";

    /// <summary>
    /// The general actions in the order Settings lists them, with fixed English labels (the UI translates them).
    /// The one list of them: Settings' rows, hotkey names and the registered hotkeys all come from here.
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Label)> General = new[]
    {
        (ToggleFilter, "Filter on/off"), (StopSounds, "Stop all sounds"), (MuteMic, "Mute mic"),
    };

    /// <summary>The English label of a general action ("muteMic" → "Mute mic"), or null for an unknown id.</summary>
    public static string? GeneralLabel(string id)
    {
        foreach (var (gid, label) in General)
            if (gid == id) return label;
        return null;
    }

    public static IReadOnlyList<(string ActionId, Hotkey Key)> All(AppSettings s)
    {
        var list = new List<(string, Hotkey)>();
        foreach (var v in s.Voices)
            if (!v.Hidden && Hotkey.TryParse(v.Hotkey, out var k)) list.Add(("voice:" + v.Id, k));
        foreach (var x in s.Sounds)
            if (!x.Hidden && Hotkey.TryParse(x.Hotkey, out var k)) list.Add(("sound:" + x.Id, k));
        foreach (var (id, _) in General)
            if (s.GeneralHotkeys.TryGetValue(id, out var t) && Hotkey.TryParse(t, out var k)) list.Add(("general:" + id, k));
        return list;
    }

    /// <summary>The action id ("voice:robot", "general:muteMic"…) of the other item that already uses the key, or null.</summary>
    public static string? WhoUsesAction(AppSettings s, Hotkey key, string exceptActionId)
    {
        foreach (var (id, k) in All(s))
            if (k == key && id != exceptActionId) return id;
        return null;
    }
}
