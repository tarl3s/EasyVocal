using System;
using System.Collections.Generic;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Windows;

/// <summary>The real devices: <see cref="DeviceList"/>, WASAPI microphones and speakers, <see cref="DefaultDevices"/>.</summary>
public sealed class WindowsAudioBackend : IAudioBackend
{
    public static readonly WindowsAudioBackend Instance = new(canSetDefaults: true);

    /// <summary>The real devices, except that Windows' defaults are never changed (the smoke test runs on these).</summary>
    public static readonly WindowsAudioBackend ReadOnlyDefaults = new(canSetDefaults: false);

    private readonly bool canSetDefaults;

    private WindowsAudioBackend(bool canSetDefaults) => this.canSetDefaults = canSetDefaults;

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

    /// <summary>Raised on a Windows audio thread (see <see cref="DeviceList.Changed"/>).</summary>
    public event Action? Changed
    {
        add => DeviceList.Changed += value;
        remove => DeviceList.Changed -= value;
    }
}
