using EasyVocal.Core.Engine;

namespace EasyVocal.Tests;

public class DeviceRulesTests
{
    private static readonly CableNames Vb = CableNames.VbCable;

    private static DeviceInfo D(string name, bool isDefault = false) => new(name.ToLowerInvariant(), name, isDefault);

    [Fact]
    public void FindVirtualCable_FindsCableInput()
    {
        var found = DeviceRules.FindVirtualCable(Vb, new[] { D("Speakers (Realtek)"), D("CABLE Input (VB-Audio Virtual Cable)") });
        Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", found?.Name);
    }

    [Fact]
    public void FindVirtualCable_NoneInstalled_ReturnsNull()
    {
        Assert.Null(DeviceRules.FindVirtualCable(Vb, new[] { D("Speakers (Realtek)"), D("Headphones (USB)") }));
        Assert.Null(DeviceRules.FindVirtualCable(Vb, new DeviceInfo[0]));
    }

    [Fact]
    public void FindVirtualCable_PrefersVbAudio_AndIgnoresCase()
    {
        var found = DeviceRules.FindVirtualCable(Vb, new[] { D("cable input (Some Other Cable)"), D("cable input (vb-audio virtual cable)") });
        Assert.Equal("cable input (vb-audio virtual cable)", found?.Name);
        Assert.Equal("cable input (Some Other Cable)",
            DeviceRules.FindVirtualCable(Vb, new[] { D("Speakers"), D("cable input (Some Other Cable)") })?.Name);
    }

    [Fact]
    public void FindVirtualCable_IgnoresTheCaptureEnd()
    {
        Assert.Null(DeviceRules.FindVirtualCable(Vb, new[] { D("CABLE Output (VB-Audio Virtual Cable)") }));
    }

    [Fact]
    public void DefaultMic_SkipsVirtualCable()
    {
        var mic = DeviceRules.DefaultMic(Vb, new[] { D("CABLE Output (VB-Audio Virtual Cable)", isDefault: true), D("Microphone (Realtek)") });
        Assert.Equal("Microphone (Realtek)", mic?.Name);
    }

    [Fact]
    public void DefaultMic_PrefersTheWindowsDefault()
    {
        var mic = DeviceRules.DefaultMic(Vb, new[] { D("Microphone (Realtek)"), D("Headset (USB)", isDefault: true) });
        Assert.Equal("Headset (USB)", mic?.Name);
    }

    [Fact]
    public void DefaultMic_OnlyCable_ReturnsNull()
    {
        Assert.Null(DeviceRules.DefaultMic(Vb, new[] { D("CABLE Output (VB-Audio Virtual Cable)", isDefault: true) }));
        Assert.Null(DeviceRules.DefaultMic(Vb, new DeviceInfo[0]));
    }

    [Fact]
    public void IsVirtualCable_RecognisesBothEnds()
    {
        Assert.True(DeviceRules.IsVirtualCable(Vb, "CABLE Input (VB-Audio Virtual Cable)"));
        Assert.True(DeviceRules.IsVirtualCable(Vb, "CABLE Output (VB-Audio Virtual Cable)"));
        Assert.True(DeviceRules.IsVirtualCable(Vb, "cable output (vb-audio virtual cable)"));
        Assert.False(DeviceRules.IsVirtualCable(Vb, "Speakers"));
        Assert.False(DeviceRules.IsVirtualCable(Vb, ""));
        Assert.False(DeviceRules.IsVirtualCable(Vb, null!));
    }

    // ---------- picking devices ----------

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MicPicker_SavedMicConnected_IsUsed()
    {
        var inputs = new[] { D("Mic A", isDefault: true), D("Mic B") };
        var pick = new MicPicker(Vb).Pick("mic b", inputs, T0);
        Assert.Equal(new MicPick("mic b", false, "Mic B"), pick);
    }

    [Fact]
    public void MicPicker_NoChoice_UsesTheDefaultRealMic()
    {
        var inputs = new[] { D("CABLE Output (VB-Audio Virtual Cable)", isDefault: true), D("Mic B") };
        Assert.Equal("mic b", new MicPicker(Vb).Pick(null, inputs, T0).Id);
        Assert.Null(new MicPicker(Vb).Pick(null, new DeviceInfo[0], T0).Id);
    }

    [Fact]
    public void MicPicker_SavedCableEnd_IsIgnored()
    {
        var inputs = new[] { D("CABLE Output (VB-Audio Virtual Cable)"), D("Mic B", isDefault: true) };
        var pick = new MicPicker(Vb).Pick("cable output (vb-audio virtual cable)", inputs, T0);
        Assert.Equal("mic b", pick.Id);
        Assert.False(pick.Fallback);
    }

    [Fact]
    public void MicPicker_SavedMicGone_KeepsTryingThenFallsBackAfterTenSeconds()
    {
        var picker = new MicPicker(Vb);
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
        var pick = new MicPicker(Vb).Pick("usb mic", new[] { D("CABLE Output (VB-Audio Virtual Cable)") }, T0.AddMinutes(5));
        Assert.Equal("usb mic", pick.Id);
        Assert.False(pick.Fallback);
    }

    [Fact]
    public void PickMonitor_DefaultRealOutput_NeverTheCallOrTheCable()
    {
        var outputs = new[] { D("CABLE Input (VB-Audio Virtual Cable)", isDefault: true), D("Speakers"), D("Headphones") };
        Assert.Equal("speakers", DeviceRules.PickMonitor(Vb, null, outputs, "cable input (vb-audio virtual cable)"));
        Assert.Equal("headphones", DeviceRules.PickMonitor(Vb, "headphones", outputs, null));
        Assert.Null(DeviceRules.PickMonitor(Vb, "speakers", outputs, "speakers")); // never into the call
        Assert.Null(DeviceRules.PickMonitor(Vb, "unplugged", outputs, null));
        Assert.Null(DeviceRules.PickMonitor(Vb, null, new[] { D("CABLE Input (VB-Audio Virtual Cable)") }, null));
    }

    [Fact]
    public void PickCall_SavedOrTheCable()
    {
        var outputs = new[] { D("Speakers"), D("CABLE Input (VB-Audio Virtual Cable)") };
        Assert.Equal("cable input (vb-audio virtual cable)", DeviceRules.PickCall(Vb, null, outputs));
        Assert.Equal("mine", DeviceRules.PickCall(Vb, "mine", outputs));
        Assert.Null(DeviceRules.PickCall(Vb, null, new[] { D("Speakers") }));
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

    [Fact]
    public void ShouldSwitchMic_OnlyToAnotherConnectedMic()
    {
        Assert.False(AudioRoutes.ShouldSwitchMic("laptop mic", new MicPick("laptop mic", true, "Laptop mic")));
        Assert.True(AudioRoutes.ShouldSwitchMic("laptop mic", new MicPick("usb mic", false, "USB mic"))); // the chosen one is back
        Assert.False(AudioRoutes.ShouldSwitchMic("laptop mic", new MicPick("usb mic", false, null))); // chosen, still missing
        Assert.False(AudioRoutes.ShouldSwitchMic("laptop mic", new MicPick(null, false, null)));      // no mic at all
    }

    [Fact]
    public void ShouldSwitchMic_StandInSession_SwitchesBackWhenTheChosenMicReturns()
    {
        var picker = new MicPicker(Vb);
        var standInOnly = new[] { D("Laptop mic", isDefault: true) };
        picker.Pick("usb mic", standInOnly, T0);
        var standIn = picker.Pick("usb mic", standInOnly, T0.AddSeconds(10));
        Assert.True(standIn.Fallback);
        Assert.False(AudioRoutes.ShouldSwitchMic(standIn.Id, picker.Pick("usb mic", standInOnly, T0.AddSeconds(30))));

        var back = new[] { D("Laptop mic", isDefault: true), D("USB mic") };
        Assert.True(AudioRoutes.ShouldSwitchMic(standIn.Id, picker.Pick("usb mic", back, T0.AddSeconds(40))));
    }

    // ---------- other platforms' cables ----------

    [Fact]
    public void BlackHole_IsTheCallDevice_AndNeverAMicOrHeadphones()
    {
        var bh = CableNames.BlackHole;
        var outputs = new[]
        {
            new DeviceInfo("bh2", "BlackHole 2ch", true), // the system default may be the cable
            new DeviceInfo("mbp", "MacBook Pro Speakers", false),
            new DeviceInfo("bh16", "BlackHole 16ch", false),
        };
        Assert.Equal("bh2", DeviceRules.FindVirtualCable(bh, outputs)?.Id);
        Assert.Equal("mbp", DeviceRules.PickMonitor(bh, null, outputs, "bh2"));
        Assert.Equal(new[] { "mbp" }, DeviceRules.ExcludeVirtualCable(bh, outputs).Select(d => d.Id));
        var inputs = new[] { new DeviceInfo("bh2", "BlackHole 2ch", true), new DeviceInfo("mic", "MacBook Pro Microphone", false) };
        Assert.Equal("mic", DeviceRules.DefaultMic(bh, inputs)?.Id);
        Assert.Equal("mic", new MicPicker(bh).Pick("bh2", inputs, T0).Id); // a saved pick of the cable is ignored
        // VB-CABLE's names mean nothing on a Mac, and BlackHole's nothing on Windows.
        Assert.Null(DeviceRules.FindVirtualCable(bh, new[] { new DeviceInfo("vb", "CABLE Input (VB-Audio Virtual Cable)", false) }));
        Assert.Null(DeviceRules.FindVirtualCable(Vb, outputs));
    }

    [Fact]
    public void LinuxDevices_TheSinkTakesTheVoice_ItsMonitorAndTheMicAreNeverPicked()
    {
        var ev = CableNames.EasyVocalLinux;
        var outputs = new[]
        {
            new DeviceInfo("alsa_output.pci.analog-stereo", "Built-in Audio Analog Stereo", true),
            new DeviceInfo("easyvocal_out", "EasyVocal-Output", false),
        };
        var inputs = new[]
        {
            new DeviceInfo("easyvocal_mic", "EasyVocal-Microphone", true),
            new DeviceInfo("easyvocal_out.monitor", "Monitor of EasyVocal-Output", false),
            new DeviceInfo("alsa_input.usb-mic", "USB Microphone", false),
        };
        Assert.Equal("easyvocal_out", DeviceRules.FindVirtualCable(ev, outputs)?.Id);
        Assert.Equal("alsa_output.pci.analog-stereo", DeviceRules.PickMonitor(ev, null, outputs, "easyvocal_out"));
        Assert.Equal("alsa_input.usb-mic", DeviceRules.DefaultMic(ev, inputs)?.Id);
        Assert.Equal(new[] { "alsa_input.usb-mic" }, DeviceRules.ExcludeVirtualCable(ev, inputs).Select(d => d.Id));
        Assert.True(DefaultDeviceRules.TakenByCable(ev, new DefaultIds("easyvocal_out", "easyvocal_out"), outputs));
    }
}
