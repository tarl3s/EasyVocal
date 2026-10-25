using EasyVocal.Core.Engine;

namespace EasyVocal.Tests;

/// <summary>VB-CABLE taking Windows' default devices: what is recognised, remembered, put back and fixed.</summary>
public class DefaultDeviceRulesTests
{
    private static readonly CableNames Vb = CableNames.VbCable;

    private const string CableIn = "CABLE Input (VB-Audio Virtual Cable)";
    private const string CableOut = "CABLE Output (VB-Audio Virtual Cable)";
    private const string Cable16 = "CABLE In 16ch (VB-Audio Virtual Cable)";

    private static DeviceInfo D(string name, bool isDefault = false) => new(name.ToLowerInvariant(), name, isDefault);

    private static string Id(string name) => name.ToLowerInvariant();

    private static DefaultIds Both(string name) => new(Id(name), Id(name));

    [Fact]
    public void IsVirtualCable_RecognisesThe16ChannelEndAndAnyVbAudioVirtualCableName()
    {
        Assert.True(DeviceRules.IsVirtualCable(Vb, Cable16));
        Assert.True(DeviceRules.IsVirtualCable(Vb, "cable in 16ch (vb-audio virtual cable)"));
        Assert.True(DeviceRules.IsVirtualCable(Vb, "Something new (VB-Audio Virtual Cable)"));
        Assert.False(DeviceRules.IsVirtualCable(Vb, "Speakers (Realtek(R) Audio)"));
        Assert.False(DeviceRules.IsVirtualCable(Vb, "Headphones (VB-Audio Voicemeeter VAIO)")); // another VB-Audio product, not the cable
    }

    [Fact]
    public void The16ChannelEnd_IsNeverTheCallNorOfferedAsHeadphones()
    {
        Assert.Equal(CableIn, DeviceRules.FindVirtualCable(Vb, new[] { D(Cable16), D(CableIn) })?.Name);
        Assert.Null(DeviceRules.FindVirtualCable(Vb, new[] { D(Cable16) }));
        var listed = DeviceRules.ExcludeVirtualCable(Vb, new[] { D("Speakers"), D(Cable16), D(CableIn) });
        Assert.Equal(new[] { "Speakers" }, listed.Select(d => d.Name));
    }

    [Fact]
    public void BestReal_RememberedThenDefaultThenNamedThenFirst_NeverTheCable()
    {
        var outputs = new[] { D(CableIn, isDefault: true), D("Digital Output (S/PDIF)"), D("Headphones (USB)"), D("Speakers (Realtek)") };
        Assert.Equal("Headphones (USB)", DeviceRules.BestReal(Vb, outputs, null, playback: true)?.Name);
        Assert.Equal("Speakers (Realtek)", DeviceRules.BestReal(Vb, outputs, Id("Speakers (Realtek)"), playback: true)?.Name);
        Assert.Equal("Headphones (USB)", DeviceRules.BestReal(Vb, outputs, Id(CableIn), playback: true)?.Name); // a remembered cable is ignored
        Assert.Equal("Headphones (USB)", DeviceRules.BestReal(Vb, outputs, "unplugged", playback: true)?.Name);

        var realDefault = new[] { D("Digital Output (S/PDIF)", isDefault: true), D("Headphones (USB)") };
        Assert.Equal("Digital Output (S/PDIF)", DeviceRules.BestReal(Vb, realDefault, null, playback: true)?.Name);
        Assert.Equal("HDMI (NVIDIA)", DeviceRules.BestReal(Vb, new[] { D(Cable16, isDefault: true), D("HDMI (NVIDIA)") }, null, playback: true)?.Name);
        Assert.Null(DeviceRules.BestReal(Vb, new[] { D(CableIn, isDefault: true), D(Cable16) }, null, playback: true));
        Assert.Null(DeviceRules.BestReal(Vb, new DeviceInfo[0], null, playback: true));
    }

    [Fact]
    public void BestReal_Recording_PrefersAMicrophoneName()
    {
        var inputs = new[] { D(CableOut, isDefault: true), D("Stereo Mix (Realtek)"), D("Microfone (USB Audio)") };
        Assert.Equal("Microfone (USB Audio)", DeviceRules.BestReal(Vb, inputs, null, playback: false)?.Name);
        Assert.Equal("Stereo Mix (Realtek)", DeviceRules.BestReal(Vb, new[] { D(CableOut, isDefault: true), D("Stereo Mix (Realtek)") }, null, playback: false)?.Name);
    }

    [Fact]
    public void PickMonitor_DefaultIsTheCable_PicksTheBestRealOutput()
    {
        var outputs = new[] { D(CableIn, isDefault: true), D("Digital Output (S/PDIF)"), D("Headphones (USB)") };
        Assert.Equal(Id("Headphones (USB)"), DeviceRules.PickMonitor(Vb, null, outputs, Id(CableIn)));
        var sixteen = new[] { D(Cable16, isDefault: true), D("Speakers (Realtek)"), D(CableIn) };
        Assert.Equal(Id("Speakers (Realtek)"), DeviceRules.PickMonitor(Vb, null, sixteen, Id(CableIn)));
        // A saved pick that is the cable (older versions listed "CABLE In 16ch") is no pick.
        Assert.Equal(Id("Speakers (Realtek)"), DeviceRules.PickMonitor(Vb, Id(Cable16), sixteen, Id(CableIn)));
    }

    [Fact]
    public void TakenByCable_EitherRole_UnknownIdsDontCount()
    {
        var outputs = new[] { D("Headphones"), D(CableIn), D(Cable16) };
        Assert.True(DefaultDeviceRules.TakenByCable(Vb, Both(CableIn), outputs));
        Assert.True(DefaultDeviceRules.TakenByCable(Vb, new DefaultIds(Id("Headphones"), Id(Cable16)), outputs));
        Assert.True(DefaultDeviceRules.TakenByCable(Vb, new DefaultIds(Id(Cable16), Id("Headphones")), outputs));
        Assert.False(DefaultDeviceRules.TakenByCable(Vb, Both("Headphones"), outputs));
        Assert.False(DefaultDeviceRules.TakenByCable(Vb, new DefaultIds(null, null), outputs));
        Assert.False(DefaultDeviceRules.TakenByCable(Vb, new DefaultIds("not listed", null), outputs));
    }

    [Fact]
    public void Remember_TheDefaultUnlessItIsTheCable()
    {
        var outputs = new[] { D("Headphones"), D(CableIn) };
        Assert.Equal(Id("Headphones"), DefaultDeviceRules.Remember(Vb, Both("Headphones"), outputs));
        Assert.Null(DefaultDeviceRules.Remember(Vb, Both(CableIn), outputs));
        Assert.Equal(Id("Headphones"), DefaultDeviceRules.Remember(Vb, new DefaultIds(Id(CableIn), Id("Headphones")), outputs));
        Assert.Null(DefaultDeviceRules.Remember(Vb, new DefaultIds(null, null), outputs));
    }

    [Fact]
    public void RestoreTo_OnlyTheRememberedDevice_OnlyWhenTheCableTookTheDefault()
    {
        var outputs = new[] { D("Speakers"), D("Headphones"), D(CableIn) };
        Assert.Equal(Id("Headphones"), DefaultDeviceRules.RestoreTo(Vb, Both(CableIn), outputs, Id("Headphones")));
        Assert.Null(DefaultDeviceRules.RestoreTo(Vb, Both("Speakers"), outputs, Id("Headphones")));    // the person picked another real one: left alone
        Assert.Null(DefaultDeviceRules.RestoreTo(Vb, Both(CableIn), outputs, null));                   // nothing remembered: no guessing
        Assert.Null(DefaultDeviceRules.RestoreTo(Vb, Both(CableIn), outputs, "unplugged"));            // remembered one gone: no guessing
        Assert.Null(DefaultDeviceRules.RestoreTo(Vb, Both(CableIn), outputs, Id(CableIn)));            // never "back" to the cable
        Assert.Equal(Id("Headphones"), DefaultDeviceRules.RestoreTo(Vb, new DefaultIds(Id("Headphones"), Id(CableIn)), outputs, Id("Headphones")));
    }

    [Fact]
    public void FixTo_RememberedElseBestReal_NothingWhenNotTaken()
    {
        var outputs = new[] { D(CableIn, isDefault: true), D("HDMI (NVIDIA)"), D("Speakers (Realtek)") };
        Assert.Equal(Id("HDMI (NVIDIA)"), DefaultDeviceRules.FixTo(Vb, Both(CableIn), outputs, Id("HDMI (NVIDIA)"), playback: true));
        Assert.Equal(Id("Speakers (Realtek)"), DefaultDeviceRules.FixTo(Vb, Both(CableIn), outputs, null, playback: true));
        Assert.Null(DefaultDeviceRules.FixTo(Vb, Both("Speakers (Realtek)"), outputs, null, playback: true));
        Assert.Null(DefaultDeviceRules.FixTo(Vb, Both(CableIn), new[] { D(CableIn, isDefault: true) }, null, playback: true));

        var inputs = new[] { D(CableOut, isDefault: true), D("Microphone (Realtek)") };
        Assert.Equal(Id("Microphone (Realtek)"), DefaultDeviceRules.FixTo(Vb, Both(CableOut), inputs, null, playback: false));
    }
}
