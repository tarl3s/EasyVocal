using System;
using System.Collections.Generic;
using EasyVocal.Core.Engine;

namespace EasyVocal.Audio;

/// <summary>
/// Where the controller gets its devices: the list Windows reports, the microphone / playback devices it opens and
/// Windows' default devices. The app uses <see cref="WindowsAudioDevices"/>; the e2e test swaps in devices that play
/// and record known audio (and fake defaults, so a test never changes the PC's real ones).
/// </summary>
public interface IAudioDevices
{
    /// <summary>Active microphones. Never throws.</summary>
    IReadOnlyList<DeviceInfo> Inputs();

    /// <summary>Active playback devices. Never throws.</summary>
    IReadOnlyList<DeviceInfo> Outputs();

    /// <summary>Opens a microphone by endpoint id. Throws when it can't be opened (unplugged, busy).</summary>
    IAudioInput OpenMic(string id);

    /// <summary>Opens a playback device by endpoint id. Throws when it can't be opened.</summary>
    IAudioOutput OpenSpeaker(string id);

    /// <summary>Windows' default playback (or recording) device, for everything else and for calls. Never throws.</summary>
    DefaultIds Defaults(bool playback);

    /// <summary>Makes this device Windows' default for every role (playback or recording, as the device is). False when it didn't work. Never throws.</summary>
    bool SetDefault(string id);

    /// <summary>The device list may have changed. Raised on any thread (see <see cref="DeviceList.Changed"/>): post to the UI thread.</summary>
    event Action? Changed;
}

/// <summary>The real devices: <see cref="DeviceList"/>, WASAPI microphones and speakers, <see cref="DefaultDevices"/>.</summary>
public sealed class WindowsAudioDevices : IAudioDevices
{
    public static readonly WindowsAudioDevices Instance = new(canSetDefaults: true);

    /// <summary>The real devices, except that Windows' defaults are never changed (the smoke test runs on these).</summary>
    public static readonly WindowsAudioDevices ReadOnlyDefaults = new(canSetDefaults: false);

    private readonly bool canSetDefaults;

    private WindowsAudioDevices(bool canSetDefaults) => this.canSetDefaults = canSetDefaults;

    public IReadOnlyList<DeviceInfo> Inputs() => DeviceList.Inputs();

    public IReadOnlyList<DeviceInfo> Outputs() => DeviceList.Outputs();

    public IAudioInput OpenMic(string id) => new WasapiMicInput(id);

    public IAudioOutput OpenSpeaker(string id) => new WasapiSpeakerOutput(id);

    public DefaultIds Defaults(bool playback) => DefaultDevices.Get(playback);

    public bool SetDefault(string id)
    {
        if (canSetDefaults) return DefaultDevices.Set(id);
        AppLog.Info("Test run: Windows' default device is not changed (" + id + ")");
        return false;
    }

    public event Action? Changed
    {
        add => DeviceList.Changed += value;
        remove => DeviceList.Changed -= value;
    }
}
