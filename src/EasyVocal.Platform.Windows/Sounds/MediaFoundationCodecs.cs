using System.IO;
using EasyVocal.Core.Sounds;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace EasyVocal.Platform.Windows;

/// <summary>
/// Windows' own decoders (Media Foundation): MP3, FLAC, M4A/AAC, WMA and the sound of a video (MP4, MOV, WMV…), and a
/// second chance for a WAV the managed reader can't read (ADPCM and other compressed WAVs).
/// </summary>
public sealed class MediaFoundationDecoder : ISoundDecoder
{
    public string Name => "Media Foundation";

    /// <summary>Everything but OGG and AIFF, which Windows doesn't decode.</summary>
    public bool CanDecode(string extension) => extension is not (".ogg" or ".aiff" or ".aif");

    public float[] Decode(string path)
    {
        // A video may carry no sound at all: Media Foundation then fails to open it (the loader says "damaged").
        using var mf = new MediaFoundationReader(path);
        return SampleReading.ToMono48k(mf.ToSampleProvider(), mf.TotalTime.TotalSeconds);
    }
}

/// <summary>MP3 export through Windows' encoder (missing on some "N" editions of Windows).</summary>
public sealed class MediaFoundationMp3Encoder : IMp3Encoder
{
    private bool? supported;

    public bool Supported
    {
        get
        {
            if (supported is { } known) return known;
            bool ok;
            try
            {
                MediaFoundationApi.Startup();
                ok = MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_MP3, SoundWriter.Pcm16, 128000) != null;
            }
            catch (Exception) { ok = false; }
            supported = ok;
            return ok;
        }
    }

    public void Write(string path, float[] samples)
    {
        MediaFoundationApi.Startup();
        using var reader = new RawSourceWaveStream(new MemoryStream(SoundWriter.ToPcm16(samples)), SoundWriter.Pcm16);
        MediaFoundationEncoder.EncodeToMp3(reader, path, 128000);
    }
}
