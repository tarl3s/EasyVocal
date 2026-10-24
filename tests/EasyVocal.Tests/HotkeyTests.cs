using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Model;

namespace EasyVocal.Tests;

public class HotkeyTests
{
    private static Hotkey K(string t) { Assert.True(Hotkey.TryParse(t, out var k), t); return k; }

    [Theory]
    [InlineData("Ctrl+7", "Ctrl+7")]
    [InlineData("ctrl+shift+F1", "Ctrl+Shift+F1")]
    [InlineData("NumPad3", "NumPad3")]
    [InlineData("Alt+Q", "Alt+Q")]
    [InlineData("7", "7")]
    [InlineData("Control+Win+Oem3", "Ctrl+Win+Oem3")]
    public void ParseThenFormat_RoundTrips(string text, string canonical)
    {
        Assert.Equal(canonical, K(text).ToString());
        Assert.Equal(K(text), K(canonical));
    }

    [Theory]
    [InlineData("")] [InlineData("Ctrl+")] [InlineData("Ctrl+Banana")] [InlineData("Shift")]
    public void Parse_Invalid_False(string t) => Assert.False(Hotkey.TryParse(t, out _));

    [Fact] public void Parse_Null_False() => Assert.False(Hotkey.TryParse(null, out _));

    [Theory]
    [InlineData("7", true)] [InlineData("A", true)] [InlineData("Shift+A", true)] [InlineData("Space", true)]
    [InlineData("OemComma", true)] [InlineData("Ctrl+7", false)] [InlineData("F1", false)] [InlineData("NumPad7", false)]
    public void IsPlainTypingKey(string t, bool expected) => Assert.Equal(expected, K(t).IsPlainTypingKey);

    [Fact]
    public void FromKeyEvent_ModifierOnly_IsNull()
    {
        foreach (var vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0xA0, 0xA3 })
            Assert.Null(Hotkey.FromKeyEvent(HotkeyMods.None, vk));
    }

    [Fact]
    public void FromKeyEvent_EscAndReserved_AreNull()
    {
        Assert.Null(Hotkey.FromKeyEvent(HotkeyMods.None, 0x1B));
        Assert.Null(Hotkey.FromKeyEvent(HotkeyMods.Win, 0x4C));
        Assert.Null(Hotkey.FromKeyEvent(HotkeyMods.Ctrl | HotkeyMods.Alt, 0x2E));
    }

    [Fact]
    public void FromKeyEvent_NormalKey()
    {
        Assert.Equal(K("Ctrl+7"), Hotkey.FromKeyEvent(HotkeyMods.Ctrl, 0x37));
        Assert.Null(Hotkey.FromKeyEvent(HotkeyMods.None, 0xFF));
    }

    private static AppSettings Settings() => new()
    {
        Voices = { new VoicePreset { Id = "robot", Name = "Robot", Hotkey = "Ctrl+1" },
                   new VoicePreset { Id = "gone", Name = "Gone", Hotkey = "Ctrl+2", Hidden = true } },
        Sounds = { new SoundItem { Id = "airhorn", Name = "Airhorn", Hotkey = "7" } },
        GeneralHotkeys = { ["muteMic"] = "F9", ["bogus"] = "F10" },
    };

    [Fact]
    public void WhoUses_FindsOtherItem()
    {
        Assert.Equal("Airhorn", HotkeyMap.WhoUses(Settings(), K("7"), "voice:robot"));
        Assert.Equal("Mute mic", HotkeyMap.WhoUses(Settings(), K("F9"), "voice:robot"));
    }

    [Fact]
    public void WhoUses_IgnoresSelfAndHidden()
    {
        Assert.Null(HotkeyMap.WhoUses(Settings(), K("7"), "sound:airhorn"));
        Assert.Null(HotkeyMap.WhoUses(Settings(), K("Ctrl+2"), "sound:airhorn"));
    }

    [Fact]
    public void All_ListsVoicesSoundsAndGeneral()
    {
        var all = HotkeyMap.All(Settings());
        Assert.Equal(new[] { "voice:robot", "sound:airhorn", "general:muteMic" }, all.Select(a => a.ActionId));
    }
}
