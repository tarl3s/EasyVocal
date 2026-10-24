using System.Text;

namespace EasyVocal.Core.Hotkeys;

/// <summary>Values match the Win32 MOD_* flags.</summary>
[Flags]
public enum HotkeyMods { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

public readonly record struct Hotkey(HotkeyMods Mods, int VirtualKey)
{
    private static readonly Dictionary<string, int> NameToVk = BuildNames();
    private static readonly Dictionary<int, string> VkToName = BuildReverse();

    private static Dictionary<string, int> BuildNames()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;
        for (var c = '0'; c <= '9'; c++) d[c.ToString()] = c;
        for (var i = 1; i <= 24; i++) d["F" + i] = 0x70 + i - 1;
        for (var i = 0; i <= 9; i++) d["NumPad" + i] = 0x60 + i;
        d["Multiply"] = 0x6A; d["Add"] = 0x6B; d["Subtract"] = 0x6D; d["Divide"] = 0x6F;
        d["Space"] = 0x20; d["Insert"] = 0x2D; d["Delete"] = 0x2E;
        d["Home"] = 0x24; d["End"] = 0x23; d["PageUp"] = 0x21; d["PageDown"] = 0x22;
        d["Left"] = 0x25; d["Up"] = 0x26; d["Right"] = 0x27; d["Down"] = 0x28;
        d["Pause"] = 0x13; d["ScrollLock"] = 0x91;
        d["Oem1"] = 0xBA; d["OemPlus"] = 0xBB; d["OemComma"] = 0xBC; d["OemMinus"] = 0xBD; d["OemPeriod"] = 0xBE;
        d["Oem2"] = 0xBF; d["Oem3"] = 0xC0; d["Oem4"] = 0xDB; d["Oem5"] = 0xDC; d["Oem6"] = 0xDD;
        d["Oem7"] = 0xDE; d["Oem8"] = 0xDF;
        return d;
    }

    private static Dictionary<int, string> BuildReverse()
    {
        var d = new Dictionary<int, string>();
        foreach (var kv in NameToVk) d[kv.Value] = kv.Key;
        return d;
    }

    public static bool TryParse(string? text, out Hotkey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = HotkeyMods.None;
        var parts = text.Split('+');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= HotkeyMods.Ctrl; break;
                case "alt": mods |= HotkeyMods.Alt; break;
                case "shift": mods |= HotkeyMods.Shift; break;
                case "win": mods |= HotkeyMods.Win; break;
                default: return false;
            }
        }
        if (!NameToVk.TryGetValue(parts[^1].Trim(), out var vk)) return false;
        key = new Hotkey(mods, vk);
        return true;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if ((Mods & HotkeyMods.Ctrl) != 0) sb.Append("Ctrl+");
        if ((Mods & HotkeyMods.Alt) != 0) sb.Append("Alt+");
        if ((Mods & HotkeyMods.Shift) != 0) sb.Append("Shift+");
        if ((Mods & HotkeyMods.Win) != 0) sb.Append("Win+");
        sb.Append(VkToName.TryGetValue(VirtualKey, out var n) ? n : "Key" + VirtualKey);
        return sb.ToString();
    }

    /// <summary>No Ctrl/Alt/Win and the key types a character: it would fire while typing in other apps.</summary>
    public bool IsPlainTypingKey =>
        (Mods & (HotkeyMods.Ctrl | HotkeyMods.Alt | HotkeyMods.Win)) == 0 &&
        (VirtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or 0x20 or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF);

    /// <summary>Null for modifier-only presses, Esc, unnamed keys, and reserved combos (Win+L, Ctrl+Alt+Del).</summary>
    public static Hotkey? FromKeyEvent(HotkeyMods mods, int vk)
    {
        if (!VkToName.ContainsKey(vk)) return null;
        if (mods == HotkeyMods.Win && vk == 'L') return null;
        if ((mods & (HotkeyMods.Ctrl | HotkeyMods.Alt)) == (HotkeyMods.Ctrl | HotkeyMods.Alt) && vk == 0x2E) return null;
        return new Hotkey(mods, vk);
    }
}
