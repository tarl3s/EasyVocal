using System;
using System.Collections.Generic;
using System.IO;
using EasyVocal.Core.Dsp;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLayer;

namespace EasyVocal.Core.Sounds;

/// <summary>Reads some kinds of sound file into mono samples at <see cref="AudioFormat.SampleRate"/>.</summary>
public interface ISoundDecoder
{
    /// <summary>For the log ("WAV/AIFF", "Media Foundation", "ffmpeg"…).</summary>
    string Name { get; }

    /// <summary>True when this decoder tries files with that extension (lower case, with the dot: ".mp3").</summary>
    bool CanDecode(string extension);

    /// <summary>
    /// The whole file as mono 48 kHz samples. Throws <see cref="SoundLoadException"/> for a final answer the person
    /// should see (too long, empty); any other exception means "not this decoder" and the next one is tried.
    /// </summary>
    float[] Decode(string path);
}

/// <summary>Which decoders <see cref="SoundLoader.Load(string)"/> uses, in order (a platform puts its own first).</summary>
public static class SoundDecoders
{
    private static volatile IReadOnlyList<ISoundDecoder> current = Portable();

    /// <summary>What this computer uses: <see cref="Portable"/> unless a platform called <see cref="Use"/>.</summary>
    public static IReadOnlyList<ISoundDecoder> Current => current;

    /// <summary>The decoders that work everywhere: WAV/AIFF, OGG, MP3, then ffmpeg (when it is installed) for the rest.</summary>
    public static IReadOnlyList<ISoundDecoder> Portable() =>
        new ISoundDecoder[] { new WavAiffDecoder(), new OggDecoder(), new Mp3Decoder(), new FfmpegDecoder() };

    /// <summary>Replaces the list (the Windows platform adds Media Foundation). Call once at startup.</summary>
    public static void Use(IReadOnlyList<ISoundDecoder> decoders) => current = decoders;
}

/// <summary>Turns any NAudio sample stream into mono 48 kHz samples, with the length limit.</summary>
public static class SampleReading
{
    /// <summary>
    /// Reads <paramref name="source"/> to its end: channels averaged, resampled to 48 kHz. <paramref name="seconds"/>
    /// (when known) sizes the buffer and refuses a too-long file before decoding it.
    /// </summary>
    public static float[] ToMono48k(ISampleProvider source, double? seconds = null)
    {
        if (seconds > SoundLoader.MaxSeconds) throw SoundLoader.TooLong();
        int channels = source.WaveFormat.Channels;
        if (channels < 1 || source.WaveFormat.SampleRate <= 0) throw new InvalidDataException("No audio channels.");
        ISampleProvider mono = channels == 1 ? source : new DownmixProvider(source);
        if (mono.WaveFormat.SampleRate != AudioFormat.SampleRate)
            mono = new WdlResamplingSampleProvider(mono, AudioFormat.SampleRate);

        long limit = (long)(SoundLoader.MaxSeconds * AudioFormat.SampleRate) + AudioFormat.SampleRate;
        long guess = (long)((seconds ?? 10) * AudioFormat.SampleRate) + 4096;
        var data = new float[(int)Math.Clamp(guess, AudioFormat.SampleRate, limit)];
        var chunk = new float[AudioFormat.SampleRate / 2];
        int total = 0;
        while (true)
        {
            int n = mono.Read(chunk, 0, chunk.Length);
            if (n <= 0) break;
            if (total + n > limit) throw SoundLoader.TooLong();
            if (total + n > data.Length)
                Array.Resize(ref data, (int)Math.Min(limit, Math.Max(data.Length * 2L, total + (long)n)));
            Array.Copy(chunk, 0, data, total, n);
            total += n;
        }
        if (total != data.Length) Array.Resize(ref data, total);
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

/// <summary>WAV (PCM or float) and AIFF, through NAudio's managed readers.</summary>
public sealed class WavAiffDecoder : ISoundDecoder
{
    public string Name => "WAV/AIFF";

    public bool CanDecode(string extension) => extension is ".wav" or ".aiff" or ".aif";

    public float[] Decode(string path)
    {
        // The readers leave a file they can't read open when given a path: the stream is ours to close.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using WaveStream reader = Path.GetExtension(path).ToLowerInvariant() == ".wav"
            ? new WaveFileReader(file)
            : new AiffFileReader(file);
        if (reader.WaveFormat.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible))
            throw new InvalidDataException("Compressed WAV (" + reader.WaveFormat.Encoding + ").");
        return SampleReading.ToMono48k(reader.ToSampleProvider(), reader.TotalTime.TotalSeconds);
    }
}

/// <summary>OGG Vorbis through NVorbis.</summary>
public sealed class OggDecoder : ISoundDecoder
{
    public string Name => "OGG (NVorbis)";

    public bool CanDecode(string extension) => extension == ".ogg";

    public float[] Decode(string path)
    {
        // VorbisWaveReader leaves a path it could not decode open, so the stream is ours to close.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var ogg = new VorbisWaveReader(file, closeOnDispose: false);
        return SampleReading.ToMono48k(ogg, ogg.TotalTime.TotalSeconds);
    }
}

/// <summary>MP3 through NLayer (managed, MIT).</summary>
public sealed class Mp3Decoder : ISoundDecoder
{
    public string Name => "MP3 (NLayer)";

    public bool CanDecode(string extension) => extension == ".mp3";

    public float[] Decode(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var mpeg = new MpegFile(file);
        if (mpeg.SampleRate <= 0 || mpeg.Channels <= 0) throw new InvalidDataException("No MPEG audio frames.");
        return SampleReading.ToMono48k(new MpegSource(mpeg), mpeg.Duration.TotalSeconds);
    }

    private sealed class MpegSource : ISampleProvider
    {
        private readonly MpegFile mpeg;

        public MpegSource(MpegFile mpeg)
        {
            this.mpeg = mpeg;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mpeg.SampleRate, mpeg.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count) => mpeg.ReadSamples(buffer, offset, count);
    }
}
