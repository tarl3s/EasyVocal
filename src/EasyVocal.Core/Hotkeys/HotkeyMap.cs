using EasyVocal.Core.Model;

namespace EasyVocal.Core.Hotkeys;

public static class HotkeyMap
{
    /// <summary>Fixed English labels for the general actions; the UI translates them.</summary>
    private static readonly (string Id, string Label)[] General =
    {
        ("toggleFilter", "Filter on/off"), ("stopSounds", "Stop all sounds"), ("muteMic", "Mute mic"),
    };

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

    /// <summary>
    /// The stored (untranslated) name of the other item that already uses the key, or null. To show it, translate
    /// only built-in names: the person's own names are shown as typed.
    /// </summary>
    public static string? WhoUses(AppSettings s, Hotkey key, string exceptActionId)
    {
        var id = WhoUsesAction(s, key, exceptActionId);
        if (id == null) return null;
        var colon = id.IndexOf(':');
        var rest = id[(colon + 1)..];
        return id[..colon] switch
        {
            "voice" => s.Voices.First(v => v.Id == rest).Name,
            "sound" => s.Sounds.First(x => x.Id == rest).Name,
            _ => General.First(g => g.Id == rest).Label,
        };
    }
}
