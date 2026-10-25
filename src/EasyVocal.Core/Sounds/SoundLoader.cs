using System;
using System.IO;
using EasyVocal.Core.Dsp;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EasyVocal.Core.Sounds;

/// <summary>A sound file could not be used. The message is a friendly English sentence meant for <c>L.T</c>.</summary>
public sealed class SoundLoadException : Exception
{
    public SoundLoadException(string message) : base(message) { }
    public SoundLoadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Reads a sound file into mono samples at <see cref="AudioFormat.SampleRate"/>: WAV, AIFF and MP3 through NAudio,
/// OGG through NVorbis, and FLAC, M4A/AAC, WMA and the sound of a video (MP4, MOV, WMV…) through Windows' own
/// decoders (Media Foundation).
/// </summary>
public static class SoundLoader
{
    /// <summary>Longest sound we accept, in seconds.</summary>
    public const double MaxSeconds = 600;

    public static readonly string[] Extensions =
    {
        ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".mp4", ".m4v", ".mov", ".wmv",
    };

    /// <summary>The extensions as a file-dialog pattern: "*.wav;*.mp3;…".</summary>
    public static string DialogPattern => "*" + string.Join(";*", Extensions);

    /// <summary>True when the file's extension is one <see cref="Load"/> reads.</summary>
    public static bool IsSupported(string? path) =>
        !string.IsNullOrEmpty(path) && Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>Files Windows decodes (Media Foundation): everything but WAV, AIFF, MP3 and OGG.</summary>
    private static bool UsesMediaFoundation(string ext) => ext is not (".wav" or ".mp3" or ".ogg" or ".aiff" or ".aif");

    public static float[] Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new SoundLoadException("We couldn't find that sound file.");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(Extensions, ext) < 0)
            throw new SoundLoadException("That kind of file isn't supported. Please pick a sound or video file (WAV, MP3, OGG, FLAC, M4A, WMA, MP4…).");

        try
        {
            if (ext == ".ogg")
            {
                using var ogg = OpenOgg(path);
                return ReadChecked(ogg, (ISampleProvider)ogg);
            }
            if (UsesMediaFoundation(ext))
            {
                // A video may carry no sound at all: Media Foundation then fails to open it (caught below).
                using var mf = new MediaFoundationReader(path);
                return ReadChecked(mf, mf.ToSampleProvider());
            }
            if (ext is ".aiff" or ".aif")
            {
                // AiffFileReader(path) leaves the file open when it can't read it: the stream is ours to close.
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var aiff = new AiffFileReader(file);
                return ReadChecked(aiff, aiff.ToSampleProvider());
            }
            using var reader = new AudioFileReader(path);
            return ReadChecked(reader, reader);
        }
        catch (SoundLoadException) { throw; }
        catch (Exception ex)
        {
            throw new SoundLoadException("We couldn't read that sound file. It may be damaged.", ex);
        }
    }

    /// <summary>VorbisWaveReader leaves a path it could not decode open, so the stream is ours to close.</summary>
    private static WaveStream OpenOgg(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { return new VorbisWaveReader(file, closeOnDispose: true); }
        catch { file.Dispose(); throw; }
    }

    private static float[] ReadChecked(WaveStream stream, ISampleProvider source)
    {
        if (stream.TotalTime.TotalSeconds > MaxSeconds) throw TooLong();
        return ReadMono(stream, source);
    }

    private static SoundLoadException TooLong() =>
        new("That sound is too long. Please pick one shorter than 10 minutes.");

    private static float[] ReadMono(WaveStream stream, ISampleProvider source)
    {
        int channels = source.WaveFormat.Channels;
        if (channels < 1) throw new SoundLoadException("We couldn't read that sound file. It may be damaged.");
        ISampleProvider mono = channels == 1 ? source : new DownmixProvider(source);
        if (mono.WaveFormat.SampleRate != AudioFormat.SampleRate)
            mono = new WdlResamplingSampleProvider(mono, AudioFormat.SampleRate);

        long limit = (long)(MaxSeconds * AudioFormat.SampleRate) + AudioFormat.SampleRate;
        long guess = (long)(stream.TotalTime.TotalSeconds * AudioFormat.SampleRate) + 4096;
        var data = new float[(int)Math.Clamp(guess, AudioFormat.SampleRate, limit)];
        var chunk = new float[AudioFormat.SampleRate / 2];
        int total = 0;
        while (true)
        {
            int n = mono.Read(chunk, 0, chunk.Length);
            if (n <= 0) break;
            if (total + n > limit) throw TooLong();
            if (total + n > data.Length)
                Array.Resize(ref data, (int)Math.Min(limit, Math.Max(data.Length * 2L, total + (long)n)));
            Array.Copy(chunk, 0, data, total, n);
            total += n;
        }
        if (total == 0) throw new SoundLoadException("That sound file is empty.");
        if (total / (double)AudioFormat.SampleRate > MaxSeconds) throw TooLong();
        if (total != data.Length) Array.Resize(ref data, total);
        for (int i = 0; i < data.Length; i++)
            if (!float.IsFinite(data[i])) data[i] = 0;
        return data;
    }

    /// <summary>Averages all channels of the source into one.</summary>
    private sealed class DownmixProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _scratch = new float[4096];

        public DownmixProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int need = count * _channels;
            if (_scratch.Length < need) _scratch = new float[need];
            int got = _source.Read(_scratch, 0, need) / _channels;
            float inv = 1f / _channels;
            for (int i = 0; i < got; i++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += _scratch[i * _channels + c];
                buffer[offset + i] = sum * inv;
            }
            return got;
        }
    }
}
