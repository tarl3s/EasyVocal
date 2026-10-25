using EasyVocal.Core.Platform;
using EasyVocal.Core.Sounds;

namespace EasyVocal.Platform.Windows;

/// <summary>The Windows implementations of EasyVocal.Core.Platform, in one place.</summary>
public static class WindowsPlatform
{
    /// <summary>
    /// Windows' decoder order: the managed WAV/AIFF and OGG readers, then Media Foundation (MP3 and everything else, as
    /// EasyVocal 0.2 did, and compressed WAVs), then NLayer for an MP3 Media Foundation refused, then ffmpeg if installed.
    /// </summary>
    public static IReadOnlyList<ISoundDecoder> Decoders() => new ISoundDecoder[]
    {
        new WavAiffDecoder(), new OggDecoder(), new MediaFoundationDecoder(), new Mp3Decoder(), new FfmpegDecoder(),
    };

    /// <summary>Makes SoundLoader and SoundWriter use Windows' decoders and MP3 encoder. Call once at startup.</summary>
    public static void UseCodecs()
    {
        SoundDecoders.Use(Decoders());
        SoundWriter.Mp3Encoder ??= new MediaFoundationMp3Encoder();
    }

    /// <summary>The real devices (and Windows' default devices).</summary>
    public static IAudioBackend Audio => WindowsAudioBackend.Instance;

    /// <summary>"Start with Windows": the current user's Run key.</summary>
    public static IStartupService Startup => StartupRegistration.Default;

    /// <summary>The Start menu shortcut.</summary>
    public static IShortcutService Shortcut => StartMenuShortcut.Default;

    /// <summary>VB-CABLE.</summary>
    public static IVirtualMicProvider VirtualMic => VbCableProvider.Instance;

    /// <summary>The release zip, EasyVocal.exe and the PowerShell swap.</summary>
    public static IUpdaterPlatform Updater => WindowsUpdaterPlatform.Instance;

    /// <summary>All of them, for the app (the tests swap some with <c>with { … }</c>).</summary>
    public static PlatformServices Services => new(Audio, Startup, Shortcut, VirtualMic, Updater);
}
