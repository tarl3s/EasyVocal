using Avalonia.Input;
using EasyVocal.Core.Hotkeys;

namespace EasyVocal;

/// <summary>
/// Avalonia's keys as the Windows virtual-key codes <see cref="Hotkey"/> stores (the saved format, on every OS; a
/// platform's hotkeys translate them to its own codes). Only keys a hotkey can use are listed.
/// </summary>
internal static class KeyMap
{
    /// <summary>The virtual-key code of <paramref name="key"/>, or 0 when a hotkey can't use it.</summary>
    public static int VirtualKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return 'A' + (key - Key.A);
        if (key is >= Key.D0 and <= Key.D9) return '0' + (key - Key.D0);
        if (key is >= Key.F1 and <= Key.F24) return 0x70 + (key - Key.F1);
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return 0x60 + (key - Key.NumPad0);
        return key switch
        {
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Subtract => 0x6D,
            Key.Divide => 0x6F,
            Key.Space => 0x20,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Pause => 0x13,
            Key.Scroll => 0x91,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            Key.Oem8 => 0xDF,
            _ => 0,
        };
    }

    /// <summary>Ctrl, Alt, Shift and Win (Meta: the Windows key, the Command key on a Mac) as the hotkey's modifiers.</summary>
    public static HotkeyMods Mods(KeyModifiers m)
    {
        var mods = HotkeyMods.None;
        if ((m & KeyModifiers.Control) != 0) mods |= HotkeyMods.Ctrl;
        if ((m & KeyModifiers.Alt) != 0) mods |= HotkeyMods.Alt;
        if ((m & KeyModifiers.Shift) != 0) mods |= HotkeyMods.Shift;
        if ((m & KeyModifiers.Meta) != 0) mods |= HotkeyMods.Win;
        return mods;
    }

    public static bool IsModifier(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>A modifier key's own flag (a key-down event doesn't count the key being pressed in its modifiers).</summary>
    public static HotkeyMods ModOf(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => HotkeyMods.Ctrl,
        Key.LeftAlt or Key.RightAlt => HotkeyMods.Alt,
        Key.LeftShift or Key.RightShift => HotkeyMods.Shift,
        Key.LWin or Key.RWin => HotkeyMods.Win,
        _ => HotkeyMods.None,
    };
}
