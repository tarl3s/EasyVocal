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
}
