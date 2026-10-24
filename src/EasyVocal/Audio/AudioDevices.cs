using System;
using System.Collections.Generic;
using EasyVocal.Core.Engine;

namespace EasyVocal.Audio;

/// <summary>
/// Where the controller gets its devices: the list Windows reports and the microphone / playback devices it opens.
/// The app uses <see cref="WindowsAudioDevices"/>; the e2e test swaps in devices that play and record known audio.
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

    /// <summary>The device list may have changed. Raised on any thread (see <see cref="DeviceList.Changed"/>).</summary>
    event Action? Changed;
}

/// <summary>The real devices: <see cref="DeviceList"/>, WASAPI microphones and speakers.</summary>
public sealed class WindowsAudioDevices : IAudioDevices
{
    public static readonly WindowsAudioDevices Instance = new();

    private WindowsAudioDevices() { }

    public IReadOnlyList<DeviceInfo> Inputs() => DeviceList.Inputs();

    public IReadOnlyList<DeviceInfo> Outputs() => DeviceList.Outputs();

    public IAudioInput OpenMic(string id) => new WasapiMicInput(id);

    public IAudioOutput OpenSpeaker(string id) => new WasapiSpeakerOutput(id);

    public event Action? Changed
    {
        add => DeviceList.Changed += value;
        remove => DeviceList.Changed -= value;
    }
}
