using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Platform;
using EasyVocal.Platform.Linux;
using EasyVocal.Platform.MacOS;
using Xunit.Abstractions;

namespace EasyVocal.Tests.Platforms;

/// <summary>The saved hotkeys (Windows virtual keys) in macOS' and X11's terms, and when Linux has no global hotkeys.</summary>
public class HotkeyMapTests(ITestOutputHelper output)
{
    private static Hotkey Key(string text) => Hotkey.TryParse(text, out var k) ? k : throw new ArgumentException(text);

    [Fact]
    public void Mac_KeysAndModifiers()
    {
        Assert.Equal(0x00u, MacKeys.KeyCode('A'));
        Assert.Equal(0x06u, MacKeys.KeyCode('Z'));
        Assert.Equal(0x1Du, MacKeys.KeyCode('0'));
        Assert.Equal(0x1Au, MacKeys.KeyCode('7'));
        Assert.Equal(0x65u, MacKeys.KeyCode(Key("F9").VirtualKey));
        Assert.Equal(0x53u, MacKeys.KeyCode(Key("NumPad1").VirtualKey));
        Assert.Equal(0x31u, MacKeys.KeyCode(Key("Space").VirtualKey));
        Assert.Equal(0x7Eu, MacKeys.KeyCode(Key("Up").VirtualKey));
        Assert.Equal(0x27u, MacKeys.KeyCode(Key("Oem7").VirtualKey)); // the quote key
        Assert.Null(MacKeys.KeyCode(Key("Pause").VirtualKey));     // no such key on a Mac keyboard
        Assert.Null(MacKeys.KeyCode(Key("F24").VirtualKey));
        // Win is Command, Alt is Option.
        Assert.Equal(MacKeys.CmdKey | MacKeys.ShiftKey, MacKeys.Modifiers(HotkeyMods.Win | HotkeyMods.Shift));
        Assert.Equal(MacKeys.ControlKey | MacKeys.OptionKey, MacKeys.Modifiers(HotkeyMods.Ctrl | HotkeyMods.Alt));
    }

    [Fact]
    public void Mac_EveryKeyCodeIsUsedOnce()
    {
        var names = new List<string>();
        for (char c = 'A'; c <= 'Z'; c++) names.Add(c.ToString());
        for (char c = '0'; c <= '9'; c++) names.Add(c.ToString());
        for (int i = 1; i <= 20; i++) names.Add("F" + i);
        for (int i = 0; i <= 9; i++) names.Add("NumPad" + i);
        names.AddRange(new[] { "Multiply", "Add", "Subtract", "Divide", "Space", "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
            "Left", "Up", "Right", "Down", "Oem1", "OemPlus", "OemComma", "OemMinus", "OemPeriod", "Oem2", "Oem3", "Oem4", "Oem5", "Oem6", "Oem7" });
        var codes = names.Select(n => MacKeys.KeyCode(Key(n).VirtualKey) ?? throw new Exception("no code for " + n)).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void X11_KeysymsAndModifiers()
    {
        Assert.Equal((uint)'a', X11Keys.Keysym('A'));
        Assert.Equal((uint)'7', X11Keys.Keysym('7'));
        Assert.Equal(0xFFC6u, X11Keys.Keysym(Key("F9").VirtualKey));
        Assert.Equal(0xFFD5u, X11Keys.Keysym(Key("F24").VirtualKey));
        Assert.Equal(0xFFB1u, X11Keys.Keysym(Key("NumPad1").VirtualKey));
        Assert.Equal(0xFF13u, X11Keys.Keysym(Key("Pause").VirtualKey));
        Assert.Equal(0x27u, X11Keys.Keysym(Key("Oem7").VirtualKey));
        Assert.Null(X11Keys.Keysym(Key("Oem8").VirtualKey));
        Assert.Equal(X11Keys.ControlMask | X11Keys.Mod1Mask, X11Keys.Modifiers(HotkeyMods.Ctrl | HotkeyMods.Alt));
        Assert.Equal(X11Keys.Mod4Mask | X11Keys.ShiftMask, X11Keys.Modifiers(HotkeyMods.Win | HotkeyMods.Shift));
        // Caps Lock and Num Lock never change which hotkey a press is.
        Assert.Equal(X11Keys.ControlMask, X11Keys.Clean(X11Keys.ControlMask | X11Keys.LockMask | X11Keys.Mod2Mask));
        Assert.Equal(new uint[] { 0, X11Keys.LockMask, X11Keys.Mod2Mask, X11Keys.LockMask | X11Keys.Mod2Mask }, X11Keys.IgnoredVariants);
    }

    [Fact]
    public void Linux_WaylandAndNoDisplay_HaveNoGlobalHotkeys()
    {
        static Func<string, string?> Env(params (string Key, string Value)[] vars) => k => vars.FirstOrDefault(v => v.Key == k).Value;

        Assert.Equal(DesktopSession.WaylandReason, DesktopSession.HotkeysUnavailable(Env(("XDG_SESSION_TYPE", "wayland"), ("DISPLAY", ":0"))));
        Assert.Equal(DesktopSession.WaylandReason, DesktopSession.HotkeysUnavailable(Env(("WAYLAND_DISPLAY", "wayland-0"))));
        Assert.Equal(DesktopSession.NoDisplayReason, DesktopSession.HotkeysUnavailable(Env()));
        Assert.Null(DesktopSession.HotkeysUnavailable(Env(("XDG_SESSION_TYPE", "x11"), ("DISPLAY", ":0"))));

        using var wayland = new X11Hotkeys(Env(("WAYLAND_DISPLAY", "wayland-0")));
        Assert.False(wayland.Available);
        Assert.Equal(DesktopSession.WaylandReason, wayland.UnavailableReason);
        Assert.False(wayland.Register("voice:robot", Key("Ctrl+5")));
        Assert.Equal(HotkeyCheck.Unknown, wayland.Check(Key("Ctrl+5")));
        Assert.Equal(0, wayland.Count);
    }

    /// <summary>A real X server (CI runs these under Xvfb): grabs, a second client refused, presses.</summary>
    [Fact]
    public void Linux_X11_GrabsTellsTakenAndPresses()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            output.WriteLine("SKIPPED: needs Linux with an X server (DISPLAY)");
            return;
        }
        var rare = Key("Ctrl+Alt+Shift+F11");
        using var first = new X11Hotkeys(k => k == "DISPLAY" ? Environment.GetEnvironmentVariable("DISPLAY") : null);
        Assert.True(first.Available, first.UnavailableReason);
        Assert.Equal(HotkeyCheck.Free, first.Check(rare));
        var pressed = new List<string>();
        first.Pressed += a => { lock (pressed) pressed.Add(a); };
        Assert.True(first.Register("voice:robot", rare));
        Assert.Equal(1, first.Count);
        Assert.Equal(HotkeyCheck.Taken, first.Check(rare));

        using var second = new X11Hotkeys(k => k == "DISPLAY" ? Environment.GetEnvironmentVariable("DISPLAY") : null);
        Assert.False(second.Register("voice:other", rare)); // X says BadAccess: another client has it
        Assert.Equal(HotkeyCheck.Taken, second.Check(rare));

        Assert.True(first.SimulateLayoutChange()); // a keyboard layout change: grabbed again, still ours
        Assert.Equal(1, first.Regrabs);
        Assert.Equal(1, first.Count);
        Assert.False(second.Register("voice:other", rare));
        Assert.True(first.SimulatePress("voice:robot"));
        lock (pressed) Assert.Equal(new[] { "voice:robot" }, pressed);
        first.UnregisterAll();
        Assert.Equal(0, first.Count);
        Assert.Equal(HotkeyCheck.Free, second.Check(rare));
    }

    [Fact]
    public void Mac_Carbon_RegistersAndUnregisters()
    {
        if (!OperatingSystem.IsMacOS())
        {
            output.WriteLine("SKIPPED: needs macOS");
            return;
        }
        using var hotkeys = new CarbonHotkeys();
        Assert.True(hotkeys.Available);
        var rare = Key("Ctrl+Alt+Shift+F13");
        var pressed = new List<string>();
        hotkeys.Pressed += pressed.Add;
        // A Mac with a session (CI's has one) must take it: a refusal here meant no hotkey ever worked on a Mac.
        Assert.True(hotkeys.Register("voice:robot", rare), "Carbon took Ctrl+Alt+Shift+F13");
        Assert.Equal(1, hotkeys.Count);
        Assert.True(hotkeys.SimulatePress("voice:robot"));
        Assert.Equal(new[] { "voice:robot" }, pressed);
        Assert.False(hotkeys.Register("voice:x", Key("Pause"))); // no such key on a Mac
        hotkeys.UnregisterAll();
        Assert.Equal(0, hotkeys.Count);
        Assert.Equal(HotkeyCheck.Free, hotkeys.Check(rare));
    }
}
