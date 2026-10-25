using EasyVocal.Core.Platform;
using EasyVocal.Core.Sounds;
using EasyVocal.Platform.Unix;

namespace EasyVocal.Platform.MacOS;

/// <summary>The macOS implementations of EasyVocal.Core.Platform, in one place.</summary>
public static class MacPlatform
{
    private static readonly Lazy<PortAudioBackend> Audio = new(() => new PortAudioBackend(CoreAudioDevices.Instance, MicPermission.IsRefused));

    /// <summary>All of them, for the app (the tests swap some with <c>with { … }</c>).</summary>
    public static PlatformServices Services => new(
        Audio.Value,
        LaunchAgent.Default,
        NoAppMenuEntry.Instance,
        BlackHoleProvider.Instance,
        UnixUpdaterPlatform.MacOS,
        SoundDecoders.Default, // WAV/AIFF, OGG, MP3 (NLayer), then ffmpeg when it is installed (Homebrew's too)
        null, // no MP3 encoder: the sound editor saves WAV
        _ => new CarbonHotkeys())
    {
        Texts = PlatformTexts.MacOS,
        OpenMicSettings = MicPermission.OpenSettings,
    };
}
