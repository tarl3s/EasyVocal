using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Storage;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

/// <summary>A backend whose microphone the test drives (or that refuses it).</summary>
internal sealed class FakeBackend : IAudioBackend
{
    public readonly FakeInput Mic = new();
    public Exception? OpenError;
    public string? OpenedId;

    public IReadOnlyList<DeviceInfo> Inputs() => new[] { new DeviceInfo("mic", "Mic", true) };
    public IReadOnlyList<DeviceInfo> Outputs() => Array.Empty<DeviceInfo>();

    public IAudioInput OpenMic(string id)
    {
        OpenedId = id;
        if (OpenError != null) throw OpenError;
        return Mic;
    }

    public IAudioOutput OpenSpeaker(string id) => new FakeOutput();
    public DefaultIds Defaults(bool playback) => new(null, null);
    public bool SetDefault(string id) => false;
    public event Action? Changed { add { } remove { } }
}

/// <summary>The setup's level bar (<see cref="MicMeter"/>) and the editor's headphone preview (<see cref="SoundPreview"/>).</summary>
public class MeterPreviewTests
{
    [Fact]
    public void MicMeter_TakesThePeakSinceTheLastCall()
    {
        var backend = new FakeBackend();
        using var meter = new MicMeter(backend, "mic");
        Assert.Equal("mic", backend.OpenedId);
        Assert.Equal(1, backend.Mic.StartCount);
        backend.Mic.Feed(new[] { 0.1f, -0.7f, 0.3f });
        backend.Mic.Feed(new[] { 0.2f });
        Assert.Equal(0.7f, meter.TakeLevel());
        Assert.Equal(0f, meter.TakeLevel()); // taken
        meter.Dispose();
        Assert.True(backend.Mic.Disposed);
        Assert.False(backend.Mic.HasListeners);
    }

    [Fact]
    public void MicMeter_AMicThatCantBeOpened_ReadsAsSilence()
    {
        var backend = new FakeBackend { OpenError = new MicPermissionDeniedException() };
        using var meter = new MicMeter(backend, "mic");
        Assert.Equal(0f, meter.TakeLevel());
    }

    [Fact]
    public void MicMeter_Bar_JumpsUpAndFallsSlowly()
    {
        Assert.Equal(0, MicMeter.NextFill(0, 0));
        Assert.Equal(1, MicMeter.NextFill(0, 1), 3);
        Assert.Equal(0.5, MicMeter.NextFill(0, 0.0316), 2); // -30 dB is half the bar
        Assert.Equal(0.96, MicMeter.NextFill(1, 0), 3);       // falls back slowly
    }

    [Fact]
    public void SoundPreview_PlaysTheWholeSoundAtItsVolume_ThenClosesTheDevice()
    {
        var output = new FakeOutput();
        var sound = Signals.Sine(440, 0.2);
        using var ended = new ManualResetEventSlim();
        var preview = new SoundPreview(output, sound, 0.5f, ended.Set);
        Assert.True(ended.Wait(5000), "the preview ended");
        Assert.Equal(1, output.StartCount);
        Assert.True(output.Disposed);
        var written = output.Written;
        Assert.Equal(sound.Length, written.Length);
        Assert.Equal(sound[123] * 0.5f, written[123], 5);
        Assert.Equal(sound.Length, preview.PlayedSamples);
        preview.Dispose();
    }

    [Fact]
    public void SoundPreview_Stopped_EndsEarly_AndAGoneDeviceJustEnds()
    {
        var output = new FakeOutput { BufferedSamples = int.MaxValue }; // never drains: the preview would wait forever
        using var ended = new ManualResetEventSlim();
        var preview = new SoundPreview(output, Signals.Sine(440, 5), 1f, ended.Set);
        Thread.Sleep(50);
        preview.Dispose();
        Assert.True(ended.Wait(2000));
        Assert.True(output.Disposed);

        var gone = new FakeOutput { ThrowOnWrite = true };
        using var ended2 = new ManualResetEventSlim();
        using var p2 = new SoundPreview(gone, Signals.Sine(440, 1), 1f, ended2.Set);
        Assert.True(ended2.Wait(2000));
        Assert.True(gone.Disposed);
    }
}

/// <summary>Small portable rules that differ by operating system.</summary>
public class PortablePathTests
{
    [Fact]
    public void PathCompare_IsCaseSensitiveOnlyOnLinux()
    {
        Assert.True(PathCompare.Same("/a/B", "/a/B"));
        Assert.Equal(!OperatingSystem.IsLinux(), PathCompare.Same("/a/B", "/A/b"));
        Assert.True(PathCompare.IsUnder("/root/x/y", "/root/"));
        Assert.Equal(!OperatingSystem.IsLinux(), PathCompare.IsUnder("/ROOT/x", "/root/"));
    }

    [Fact]
    public void DataFolder_IsApplicationSupportOnMac_AndAppDataElsewhere()
    {
        Assert.Equal(Path.Combine("/Users/z", "Library", "Application Support", "EasyVocal"),
            SettingsStore.DefaultFolderFor(macOS: true, "/Users/z", "/Users/z/.config"));
        Assert.Equal(Path.Combine("/home/z/.config", "EasyVocal"), SettingsStore.DefaultFolderFor(macOS: false, "/home/z", "/home/z/.config"));
        Assert.EndsWith("EasyVocal", SettingsStore.DefaultFolder);
    }
}
