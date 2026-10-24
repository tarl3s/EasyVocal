using EasyVocal.Core.Engine;

namespace EasyVocal.Tests;

public class DeviceRulesTests
{
    private static DeviceInfo D(string name, bool isDefault = false) => new(name.ToLowerInvariant(), name, isDefault);

    [Fact]
    public void FindVirtualCable_FindsCableInput()
    {
        var found = DeviceRules.FindVirtualCable(new[] { D("Speakers (Realtek)"), D("CABLE Input (VB-Audio Virtual Cable)") });
        Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", found?.Name);
    }

    [Fact]
    public void FindVirtualCable_NoneInstalled_ReturnsNull()
    {
        Assert.Null(DeviceRules.FindVirtualCable(new[] { D("Speakers (Realtek)"), D("Headphones (USB)") }));
        Assert.Null(DeviceRules.FindVirtualCable(new DeviceInfo[0]));
    }

    [Fact]
    public void FindVirtualCable_PrefersVbAudio_AndIgnoresCase()
    {
        var found = DeviceRules.FindVirtualCable(new[] { D("cable input (Some Other Cable)"), D("cable input (vb-audio virtual cable)") });
        Assert.Equal("cable input (vb-audio virtual cable)", found?.Name);
        Assert.Equal("cable input (Some Other Cable)",
            DeviceRules.FindVirtualCable(new[] { D("Speakers"), D("cable input (Some Other Cable)") })?.Name);
    }

    [Fact]
    public void FindVirtualCable_IgnoresTheCaptureEnd()
    {
        Assert.Null(DeviceRules.FindVirtualCable(new[] { D("CABLE Output (VB-Audio Virtual Cable)") }));
    }

    [Fact]
    public void DefaultMic_SkipsVirtualCable()
    {
        var mic = DeviceRules.DefaultMic(new[] { D("CABLE Output (VB-Audio Virtual Cable)", isDefault: true), D("Microphone (Realtek)") });
        Assert.Equal("Microphone (Realtek)", mic?.Name);
    }

    [Fact]
    public void DefaultMic_PrefersTheWindowsDefault()
    {
        var mic = DeviceRules.DefaultMic(new[] { D("Microphone (Realtek)"), D("Headset (USB)", isDefault: true) });
        Assert.Equal("Headset (USB)", mic?.Name);
    }

    [Fact]
    public void DefaultMic_OnlyCable_ReturnsNull()
    {
        Assert.Null(DeviceRules.DefaultMic(new[] { D("CABLE Output (VB-Audio Virtual Cable)", isDefault: true) }));
        Assert.Null(DeviceRules.DefaultMic(new DeviceInfo[0]));
    }

    [Fact]
    public void IsVirtualCable_RecognisesBothEnds()
    {
        Assert.True(DeviceRules.IsVirtualCable("CABLE Input (VB-Audio Virtual Cable)"));
        Assert.True(DeviceRules.IsVirtualCable("CABLE Output (VB-Audio Virtual Cable)"));
        Assert.True(DeviceRules.IsVirtualCable("cable output (vb-audio virtual cable)"));
        Assert.False(DeviceRules.IsVirtualCable("Speakers"));
        Assert.False(DeviceRules.IsVirtualCable(""));
        Assert.False(DeviceRules.IsVirtualCable(null!));
    }

    // ---------- picking devices ----------

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MicPicker_SavedMicConnected_IsUsed()
    {
        var inputs = new[] { D("Mic A", isDefault: true), D("Mic B") };
        var pick = new MicPicker().Pick("mic b", inputs, T0);
        Assert.Equal(new MicPick("mic b", false, "Mic B"), pick);
    }

    [Fact]
    public void MicPicker_NoChoice_UsesTheDefaultRealMic()
    {
        var inputs = new[] { D("CABLE Output (VB-Audio Virtual Cable)", isDefault: true), D("Mic B") };
        Assert.Equal("mic b", new MicPicker().Pick(null, inputs, T0).Id);
        Assert.Null(new MicPicker().Pick(null, new DeviceInfo[0], T0).Id);
    }

    [Fact]
    public void MicPicker_SavedCableEnd_IsIgnored()
    {
        var inputs = new[] { D("CABLE Output (VB-Audio Virtual Cable)"), D("Mic B", isDefault: true) };
        var pick = new MicPicker().Pick("cable output (vb-audio virtual cable)", inputs, T0);
        Assert.Equal("mic b", pick.Id);
        Assert.False(pick.Fallback);
    }

    [Fact]
    public void MicPicker_SavedMicGone_KeepsTryingThenFallsBackAfterTenSeconds()
    {
        var picker = new MicPicker();
        var inputs = new[] { D("Laptop mic", isDefault: true) };
        Assert.Equal(new MicPick("usb mic", false, null), picker.Pick("usb mic", inputs, T0));
        Assert.Equal("usb mic", picker.Pick("usb mic", inputs, T0.AddSeconds(9)).Id);

        var later = picker.Pick("usb mic", inputs, T0.AddSeconds(10));
        Assert.Equal(new MicPick("laptop mic", true, "Laptop mic"), later);

        // It's back: the person's choice wins again at once.
        var back = new[] { D("Laptop mic", isDefault: true), D("USB mic") };
        Assert.Equal(new MicPick("usb mic", false, "USB mic"), picker.Pick("usb mic", back, T0.AddSeconds(11)));
        // Gone again: the ten seconds start over.
        Assert.False(picker.Pick("usb mic", inputs, T0.AddSeconds(12)).Fallback);
        Assert.True(picker.Pick("usb mic", inputs, T0.AddSeconds(22)).Fallback);
    }

    [Fact]
    public void MicPicker_SavedMicGone_NoOtherMic_KeepsTheChoice()
    {
        var pick = new MicPicker().Pick("usb mic", new[] { D("CABLE Output (VB-Audio Virtual Cable)") }, T0.AddMinutes(5));
        Assert.Equal("usb mic", pick.Id);
        Assert.False(pick.Fallback);
    }

    [Fact]
    public void PickMonitor_DefaultRealOutput_NeverTheCallOrTheCable()
    {
        var outputs = new[] { D("CABLE Input (VB-Audio Virtual Cable)", isDefault: true), D("Speakers"), D("Headphones") };
        Assert.Equal("speakers", DeviceRules.PickMonitor(null, outputs, "cable input (vb-audio virtual cable)"));
        Assert.Equal("headphones", DeviceRules.PickMonitor("headphones", outputs, null));
        Assert.Null(DeviceRules.PickMonitor("speakers", outputs, "speakers")); // never into the call
        Assert.Null(DeviceRules.PickMonitor("unplugged", outputs, null));
        Assert.Null(DeviceRules.PickMonitor(null, new[] { D("CABLE Input (VB-Audio Virtual Cable)") }, null));
    }

    [Fact]
    public void PickCall_SavedOrTheCable()
    {
        var outputs = new[] { D("Speakers"), D("CABLE Input (VB-Audio Virtual Cable)") };
        Assert.Equal("cable input (vb-audio virtual cable)", DeviceRules.PickCall(null, outputs));
        Assert.Equal("mine", DeviceRules.PickCall("mine", outputs));
        Assert.Null(DeviceRules.PickCall(null, new[] { D("Speakers") }));
    }

    [Fact]
    public void AudioRoutes_RestartOnlyWhenThePickChangesOrTheMonitorFailed()
    {
        var inUse = new AudioRoutes("mic", "cable", "speakers");
        Assert.False(AudioRoutes.NeedsRestart(inUse, new AudioRoutes("mic", "cable", "speakers"), monitorLost: false));
        Assert.True(AudioRoutes.NeedsRestart(inUse, new AudioRoutes("usb mic", "cable", "speakers"), false));
        Assert.True(AudioRoutes.NeedsRestart(inUse, new AudioRoutes("mic", "cable", "headphones"), false));
        Assert.True(AudioRoutes.NeedsRestart(inUse, new AudioRoutes("mic", "cable", null), false));
        Assert.True(AudioRoutes.NeedsRestart(inUse, inUse, monitorLost: true));
    }
}
