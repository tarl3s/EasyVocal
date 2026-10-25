using EasyVocal.Core.Platform;
using EasyVocal.Core.Sounds;

namespace EasyVocal.Platform.Windows;

/// <summary>The Windows implementations of EasyVocal.Core.Platform, in one place.</summary>
public static class WindowsPlatform
{
    /// <summary>
    /// Windows' decoder order, which reads every file the way EasyVocal 0.2 did first: the managed WAV/AIFF reader, ACM for
    /// compressed WAVs, NVorbis for OGG, Media Foundation (MP3 and everything else), then second chances: ACM's MP3
    /// codec, NLayer, and ffmpeg when it is installed.
    /// </summary>
    public static IReadOnlyList<ISoundDecoder> Decoders() => new ISoundDecoder[]
    {
        new WavAiffDecoder(), new AcmWavDecoder(), new OggDecoder(), new MediaFoundationDecoder(),
        new AcmMp3Decoder(), new Mp3Decoder(), new FfmpegDecoder(),
    };

    private static readonly MediaFoundationMp3Encoder Mp3 = new();

    /// <summary>All of them, for the app (the tests swap some with <c>with { … }</c>).</summary>
    public static PlatformServices Services => new(
        WindowsAudioBackend.Instance,
        StartupRegistration.Default,
        StartMenuShortcut.Default,
        VbCableProvider.Instance,
        WindowsUpdaterPlatform.Instance,
        Decoders(),
        Mp3,
        window => new WindowsGlobalHotkeys(window))
    {
        Texts = PlatformTexts.Windows,
        OpenMicSettings = OpenMicPrivacy,
    };

    /// <summary>Windows Settings, Privacy &amp; security, Microphone.</summary>
    public static void OpenMicPrivacy() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true })?.Dispose();
}
