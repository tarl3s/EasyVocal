using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Platform;
using EasyVocal.Platform.Windows;

namespace EasyVocal.Tests;

/// <summary>
/// RegisterHotKey on EasyVocal's own message-only window (no UI framework): a press goes through the window's own
/// procedure, a combination taken by the first registration is reported as taken by a second one.
/// </summary>
public class WindowsHotkeysTests
{
    /// <summary>Ctrl+Alt+Shift+F23: a combination nobody uses.</summary>
    private static readonly Hotkey Rare = new(HotkeyMods.Ctrl | HotkeyMods.Alt | HotkeyMods.Shift, 0x86);

    [Fact]
    public void OwnWindow_RegistersPressesAndTellsTakenFromFree()
    {
        using var first = (WindowsGlobalHotkeys)WindowsPlatform.Services.CreateHotkeys();
        Assert.True(first.Available);
        Assert.Null(first.UnavailableReason);
        if (first.Check(Rare) != HotkeyCheck.Free)
        {
            Console.WriteLine("Skipped: another app owns Ctrl+Alt+Shift+F23");
            return;
        }
        var pressed = new List<string>();
        first.Pressed += pressed.Add;
        Assert.True(first.Register("voice:robot", Rare));
        Assert.Equal(1, first.Count);
        Assert.Equal(1, ((IHotkeyTestHooks)first).HighestId);

        using var second = new WindowsGlobalHotkeys();
        Assert.Equal(HotkeyCheck.Taken, second.Check(Rare)); // owned by the first one now
        Assert.False(second.Register("voice:other", Rare));

        Assert.True(((IHotkeyTestHooks)first).SimulatePress("voice:robot"));
        Assert.Equal(new[] { "voice:robot" }, pressed);
        Assert.False(first.SimulatePress("voice:none"));

        first.UnregisterAll();
        Assert.Equal(0, first.Count);
        Assert.Equal(HotkeyCheck.Free, second.Check(Rare));
    }
}

public class WindowsHotkeysFailureTests
{
    [Fact]
    public void AHandlerThatThrows_DoesNotEscapeIntoWindows()
    {
        using var hotkeys = new WindowsGlobalHotkeys();
        var rare = new Hotkey(HotkeyMods.Ctrl | HotkeyMods.Alt | HotkeyMods.Shift, 0x87); // Ctrl+Alt+Shift+F24
        if (hotkeys.Check(rare) != HotkeyCheck.Free)
        {
            Console.WriteLine("Skipped: another app owns Ctrl+Alt+Shift+F24");
            return;
        }
        int calls = 0;
        hotkeys.Pressed += _ => { calls++; throw new InvalidOperationException("a handler bug"); };
        Assert.True(hotkeys.Register("general:muteMic", rare));
        // SendMessage runs the window procedure right here: an exception escaping it would end the test process.
        Assert.True(((IHotkeyTestHooks)hotkeys).SimulatePress("general:muteMic"));
        Assert.True(((IHotkeyTestHooks)hotkeys).SimulatePress("general:muteMic"));
        Assert.Equal(2, calls);
    }
}
