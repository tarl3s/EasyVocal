using EasyVocal.Core.Platform;
using EasyVocal.Platform.Unix;
using Xunit.Abstractions;

namespace EasyVocal.Tests.Platforms;

/// <summary>
/// The PortAudio backend on this computer (its native library ships for macOS, Linux and — for these tests — Windows):
/// it starts, lists without throwing, and, when there is a playback device, opens one, plays silence and closes it. A
/// fake device list checks the CoreAudio path's id → name matching. Nothing is changed on the system.
/// </summary>
public class PortAudioTests(ITestOutputHelper output)
{
    private sealed class FakeSource(params SystemDevice[] devices) : IDeviceSource
    {
        public IReadOnlyList<SystemDevice> Devices() => devices;
        public string? DefaultId(bool playback) => devices.FirstOrDefault(d => playback ? d.Output : d.Input)?.Id;
    }

    [Fact]
    public void Real_ListsAndOpensAPlaybackDevice()
    {
        using var backend = new PortAudioBackend();
        var inputs = backend.Inputs();
        var outputs = backend.Outputs();
        output.WriteLine($"PortAudio: {inputs.Count} input(s), {outputs.Count} output(s); problem: {backend.Problem ?? "none"}");
        foreach (var d in inputs.Concat(outputs)) output.WriteLine("  " + d.Id + (d.IsDefault ? " (default)" : ""));
        if (backend.Problem != null)
        {
            // The library must load wherever EasyVocal ships it; a machine without sound hardware may still fail to start it.
            Assert.DoesNotContain("Unable to load", backend.Problem);
            output.WriteLine("SKIPPED the stream part: " + backend.Problem);
            return;
        }
        Assert.Equal(inputs.Count, inputs.Select(d => d.Id).Distinct().Count());
        Assert.Equal(outputs.Count, outputs.Select(d => d.Id).Distinct().Count());
        Assert.False(backend.SetDefault(outputs.FirstOrDefault()?.Id ?? "x"));
        var defaults = backend.Defaults(playback: true);
        Assert.Equal(defaults.Console, defaults.Communications);
        if (outputs.Count == 0)
        {
            output.WriteLine("SKIPPED the stream part: no playback device");
            return;
        }
        var device = outputs.FirstOrDefault(d => d.IsDefault) ?? outputs[0];
        IAudioOutputProbe(backend, device.Id);
    }

    private void IAudioOutputProbe(PortAudioBackend backend, string id)
    {
        Core.Engine.IAudioOutput speaker;
        try { speaker = backend.OpenSpeaker(id); }
        catch (IOException ex)
        {
            output.WriteLine("SKIPPED: the device couldn't be opened here: " + ex.Message);
            return;
        }
        using (speaker)
        {
            speaker.Start();
            var silence = new float[480];
            for (int i = 0; i < 20; i++)
            {
                speaker.Write(silence);
                Thread.Sleep(10);
            }
            Assert.True(speaker.BufferedSamples < 48000 / 2, "the device takes the samples");
            speaker.TrimTo(0);
        }
        Assert.Throws<ObjectDisposedException>(() => speaker.Write(new float[10]));
    }

    [Fact]
    public void ASystemDeviceList_IsWhatTheAppSees()
    {
        var source = new FakeSource(new SystemDevice("uid-mic", "Built-in Microphone", true, false),
            new SystemDevice("uid-bh", "BlackHole 2ch", true, true), new SystemDevice("uid-spk", "Speakers", false, true));
        using var backend = new PortAudioBackend(source);
        Assert.Equal(new[] { "uid-mic", "uid-bh" }, backend.Inputs().Select(d => d.Id));
        Assert.Equal(new[] { "uid-bh", "uid-spk" }, backend.Outputs().Select(d => d.Id));
        Assert.True(backend.Inputs()[0].IsDefault);
        Assert.Equal(new Core.Engine.DefaultIds("uid-bh", "uid-bh"), backend.Defaults(playback: true));
        // A device the system lists but PortAudio doesn't know: a clear error, never a crash.
        Assert.ThrowsAny<IOException>(() => backend.OpenSpeaker("uid-not-there"));
    }

    [Fact]
    public void ARefusedMicrophone_IsMicPermissionDenied()
    {
        using var backend = new PortAudioBackend(new FakeSource(new SystemDevice("m", "Mic", true, false)), micRefused: () => true);
        Assert.Throws<MicPermissionDeniedException>(() => backend.OpenMic("m"));
    }

    [Fact]
    public void Fingerprints_ChangeWithTheDevicesAndDefaults()
    {
        var a = new[] { new SystemDevice("1", "Mic", true, false) };
        Assert.Equal(PortAudioBackend.Fingerprint(a, "1", null), PortAudioBackend.Fingerprint(a, "1", null));
        Assert.NotEqual(PortAudioBackend.Fingerprint(a, "1", null), PortAudioBackend.Fingerprint(a, null, null));
        Assert.NotEqual(PortAudioBackend.Fingerprint(a, "1", null), PortAudioBackend.Fingerprint(a.Append(new SystemDevice("2", "Headset", false, true)), "1", null));
    }
}
