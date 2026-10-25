using System.IO;
using EasyVocal.Core.Sounds;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace EasyVocal.Platform.Windows;

/// <summary>
/// Windows' own decoders (Media Foundation): MP3 (what EasyVocal 0.2 used for it, through NAudio's AudioFileReader),
/// FLAC, M4A/AAC, WMA and the sound of a video (MP4, MOV, WMV…), and a last chance for a WAV nothing else reads.
/// </summary>
public sealed class MediaFoundationDecoder : ISoundDecoder
{
    public string Name => "Media Foundation";

    /// <summary>Everything but OGG and AIFF, which Windows doesn't decode.</summary>
    public bool CanDecode(string extension) => extension is not (".ogg" or ".aiff" or ".aif");

    public float[] Decode(string path, CancellationToken ct)
    {
        // A video may carry no sound at all: Media Foundation then fails to open it (the loader says "damaged").
        using var mf = new MediaFoundationReader(path);
        return SampleReading.ToMono48k(mf.ToSampleProvider(), mf.TotalTime.TotalSeconds, ct);
    }
}

/// <summary>
/// Compressed WAVs (mu-law, A-law, ADPCM, GSM…) through Windows' ACM codecs, as EasyVocal 0.2 read them
/// (NAudio's AudioFileReader converted any non-PCM WAV with WaveFormatConversionStream).
/// </summary>
public sealed class AcmWavDecoder : ISoundDecoder
{
    public string Name => "WAV (Windows ACM)";

    public bool CanDecode(string extension) => extension == ".wav";

    public float[] Decode(string path, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new WaveFileReader(file);
        if (reader.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
            throw new InvalidDataException("Not a compressed WAV: the managed reader's job.");
        using var pcm = WaveFormatConversionStream.CreatePcmStream(reader);
        using var aligned = new BlockAlignReductionStream(pcm);
        return SampleReading.ToMono48k(aligned.ToSampleProvider(), aligned.TotalTime.TotalSeconds, ct);
    }
}

/// <summary>MP3 through Windows' ACM MP3 codec (NAudio's Mp3FileReader): a second chance after Media Foundation.</summary>
public sealed class AcmMp3Decoder : ISoundDecoder
{
    public string Name => "MP3 (Windows ACM)";

    public bool CanDecode(string extension) => extension == ".mp3";

    public float[] Decode(string path, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var mp3 = new Mp3FileReader(file);
        return SampleReading.ToMono48k(mp3.ToSampleProvider(), mp3.TotalTime.TotalSeconds, ct);
    }
}

/// <summary>MP3 export through Windows' encoder (missing on some "N" editions of Windows).</summary>
public sealed class MediaFoundationMp3Encoder : IMp3Encoder
{
    private readonly object sync = new();
    private bool? supported; // guarded by sync

    public bool Supported
    {
        get
        {
            lock (sync)
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
    }

    public void Write(string path, float[] samples)
    {
        MediaFoundationApi.Startup();
        using var reader = new RawSourceWaveStream(new MemoryStream(SoundWriter.ToPcm16(samples)), SoundWriter.Pcm16);
        MediaFoundationEncoder.EncodeToMp3(reader, path, 128000);
    }
}
