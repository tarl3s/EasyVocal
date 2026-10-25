using EasyVocal.Core.Platform;
using EasyVocal.Platform.Unix;
using Xunit.Abstractions;
using EasyVocal.Tests.Helpers;

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
        // Closed on a thread of its own once its own audio thread stopped it (never aborted from here while it runs).
        var stream = (PortAudioStream)speaker;
        Assert.True(SpinWait.SpinUntil(() => stream.Closed.IsCompleted, 10000), "the speaker closed");
        output.WriteLine("closed: " + stream.CloseRoute);
        Assert.Equal("completed", stream.CloseRoute);
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

    private static PortAudioBackend.PaDevice Pa(int index, string name, bool input) =>
        new(index, "pa:x:" + name + "#" + index, name, input ? 1 : 0, input ? 0 : 2, false, false, 0.01, 0.01, 48000);

    [Fact]
    public void SameNameDevices_AreMatchedInOrder_NotAllToTheFirst()
    {
        // Two identical USB headsets: CoreAudio tells them apart by UID, PortAudio only by order.
        var system = new[]
        {
            new SystemDevice("uid-a", "USB Headset", true, true), new SystemDevice("uid-spk", "Speakers", false, true),
            new SystemDevice("uid-b", "USB Headset", true, true),
        };
        var pa = new[] { Pa(0, "USB Headset", false), Pa(1, "USB Headset", true), Pa(2, "Speakers", false), Pa(3, "USB Headset", false), Pa(4, "USB Headset", true) };
        Assert.Equal(0, PortAudioBackend.MatchSystem(system, pa, "uid-a", input: false)?.Index);
        Assert.Equal(3, PortAudioBackend.MatchSystem(system, pa, "uid-b", input: false)?.Index);
        Assert.Equal(1, PortAudioBackend.MatchSystem(system, pa, "uid-a", input: true)?.Index);
        Assert.Equal(4, PortAudioBackend.MatchSystem(system, pa, "uid-b", input: true)?.Index);
        Assert.Equal(2, PortAudioBackend.MatchSystem(system, pa, "uid-spk", input: false)?.Index);
        Assert.Null(PortAudioBackend.MatchSystem(system, pa, "uid-spk", input: true));
        Assert.Null(PortAudioBackend.MatchSystem(system, pa.Take(2).ToArray(), "uid-b", input: true)); // the second isn't in PortAudio yet
    }

    [Fact]
    public void RateConverter_PlaysMono48kAtTheDevicesRate()
    {
        // A 1 kHz tone at 48 kHz, pulled at 44.1 kHz: the same tone, the right number of samples consumed.
        long taken = 0;
        var converter = new RateConverter(44100, span =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = MathF.Sin(2 * MathF.PI * 1000 * (taken + i) / 48000f);
            taken += span.Length;
        });
        var out44 = new float[44100];
        for (int at = 0; at < out44.Length; at += 441) converter.Pull(out44.AsSpan(at, 441));
        Assert.InRange(taken, 48000 - 512, 48000 + 512);
        // Zero crossings going up: about 1000 in a second.
        int ups = 0;
        for (int i = 1; i < out44.Length; i++) if (out44[i - 1] < 0 && out44[i] >= 0) ups++;
        Assert.InRange(ups, 995, 1005);
    }

    [Fact]
    public void Alsa_Fingerprint_ChangesWithCardsAndDeviceFiles()
    {
        var temp = TestFolders.New("EV_Test_Alsa_");
        try
        {
            var cards = Path.Combine(temp, "cards");
            var snd = Path.Combine(temp, "snd");
            Directory.CreateDirectory(snd);
            File.WriteAllText(cards, " 0 [PCH            ]: HDA-Intel - HDA Intel PCH\n");
            File.WriteAllText(Path.Combine(snd, "pcmC0D0p"), "");
            var a = EasyVocal.Platform.Linux.AlsaDevices.Fingerprint(cards, snd);
            Assert.Equal(a, EasyVocal.Platform.Linux.AlsaDevices.Fingerprint(cards, snd));
            File.WriteAllText(Path.Combine(snd, "pcmC1D0c"), ""); // a USB microphone plugged in
            Assert.NotEqual(a, EasyVocal.Platform.Linux.AlsaDevices.Fingerprint(cards, snd));
            Assert.Equal("#", EasyVocal.Platform.Linux.AlsaDevices.Fingerprint(Path.Combine(temp, "none"), Path.Combine(temp, "none")));
        }
        finally { TestFolders.Delete(temp); }
    }

    /// <summary>PortAudio is started again only when the system's devices changed — and open streams then fail, so the engine reopens.</summary>
    [Fact]
    public void Real_PortAudioStartsAgainOnlyWhenTheDevicesChange()
    {
        string look = "cards: 1";
        using var backend = new PortAudioBackend(systemFingerprint: () => look);
        var outputs = backend.Outputs();
        if (backend.Problem != null || outputs.Count == 0)
        {
            output.WriteLine("SKIPPED: PortAudio has no playback device here (" + (backend.Problem ?? "none") + ")");
            return;
        }
        Assert.Equal(1, backend.Starts);
        backend.PollNow();
        backend.PollNow();
        backend.Inputs();
        Assert.Equal(1, backend.Starts); // nothing changed: never started again on a timer

        Core.Engine.IAudioOutput speaker;
        try { speaker = backend.OpenSpeaker((outputs.FirstOrDefault(d => d.IsDefault) ?? outputs[0]).Id); }
        catch (IOException ex)
        {
            output.WriteLine("SKIPPED: " + ex.Message);
            return;
        }
        using (speaker)
        {
            speaker.Start();
            look = "cards: 2"; // a card plugged in while the speaker plays
            backend.PollNow();
            Assert.Throws<IOException>(() => speaker.Write(new float[480])); // the engine opens everything again
            Assert.Equal(1, backend.Starts); // not while a stream is open
        }
        Assert.True(backend.WaitForClosing(TimeSpan.FromSeconds(10)), "the speaker closed");
        backend.Inputs();
        Assert.Equal(2, backend.Starts); // with nothing open: started again, the new card in its list
    }
}
