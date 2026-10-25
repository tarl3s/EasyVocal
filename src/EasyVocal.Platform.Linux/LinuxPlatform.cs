using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Sounds;
using EasyVocal.Platform.Unix;

namespace EasyVocal.Platform.Linux;

/// <summary>The Linux implementations of EasyVocal.Core.Platform, in one place.</summary>
public static class LinuxPlatform
{
    private static readonly object sync = new();
    private static IAudioBackend? audio;

    /// <summary>
    /// The sound devices: through the PulseAudio protocol when a server answers (PulseAudio, or PipeWire's
    /// pipewire-pulse: almost every desktop), else PortAudio (ALSA), where EasyVocal's own virtual mic can't be reached.
    /// </summary>
    public static IAudioBackend Audio()
    {
        lock (sync)
        {
            if (audio != null) return audio;
            if (Pactl.Default.Locate() != null && PulseBackend.ServerAnswers())
            {
                audio = new PulseBackend();
                AppLog.Info("Sound: PulseAudio / PipeWire (pactl + libpulse-simple)");
            }
            else
            {
                audio = new PortAudioBackend();
                AppLog.Info("Sound: no PulseAudio / PipeWire server answered: PortAudio (ALSA); install pipewire-pulse for the virtual microphone");
            }
            return audio;
        }
    }

    /// <param name="appIcon">EasyVocal's icon (.ico bytes) for the applications menu entry; null = none.</param>
    public static PlatformServices Services(Func<byte[]?>? appIcon = null)
    {
        var backend = Audio();
        return new PlatformServices(
            backend,
            AutostartEntry.Default,
            new AppMenuEntry(icon: appIcon),
            new PactlVirtualMic(devicesChanged: () => (backend as PulseBackend)?.Invalidate()),
            UnixUpdaterPlatform.Linux,
            SoundDecoders.Default, // WAV/AIFF, OGG, MP3 (NLayer), then ffmpeg when it is installed
            null, // no MP3 encoder: the sound editor saves WAV
            _ => new X11Hotkeys())
        {
            Texts = PlatformTexts.Linux,
            OpenMicSettings = null, // no microphone permission to open on a plain Linux desktop
            HasTray = () => TrayHost.Present(),
        };
    }
}
