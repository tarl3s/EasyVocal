using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
    /// The whole file as mono 48 kHz samples. Any exception (or an empty result) means "not this decoder" and the
    /// loader tries the next one; a <see cref="SoundLoadException"/> carries the sentence to show when none works.
    /// Stops with <see cref="OperationCanceledException"/> soon after <paramref name="ct"/> fires.
    /// </summary>
    float[] Decode(string path, CancellationToken ct);
}

/// <summary>The portable decoder list (a platform builds its own: <c>PlatformServices.Decoders</c>).</summary>
public static class SoundDecoders
{
    /// <summary>The decoders that work everywhere: WAV/AIFF, OGG, MP3, then ffmpeg (when it is installed) for the rest.</summary>
    public static IReadOnlyList<ISoundDecoder> Portable() =>
        new ISoundDecoder[] { new WavAiffDecoder(), new OggDecoder(), new Mp3Decoder(), new FfmpegDecoder() };

    /// <summary>One shared <see cref="Portable"/> list (the decoders keep no per-file state).</summary>
    public static IReadOnlyList<ISoundDecoder> Default { get; } = Portable();
}

/// <summary>Turns any NAudio sample stream into mono 48 kHz samples, with the length limit.</summary>
public static class SampleReading
{
    /// <summary>
    /// Reads <paramref name="source"/> to its end: channels averaged, resampled to 48 kHz. <paramref name="seconds"/>
    /// (when known) sizes the buffer and refuses a too-long file before decoding it.
    /// </summary>
    public static float[] ToMono48k(ISampleProvider source, double? seconds = null, CancellationToken ct = default)
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
            ct.ThrowIfCancellationRequested();
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

/// <summary>WAV (PCM or float, also WAVE_FORMAT_EXTENSIBLE) and AIFF, through NAudio's managed readers.</summary>
public sealed class WavAiffDecoder : ISoundDecoder
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public string Name => "WAV/AIFF";

    public bool CanDecode(string extension) => extension is ".wav" or ".aiff" or ".aif";

    public float[] Decode(string path, CancellationToken ct)
    {
        // The readers leave a file they can't read open when given a path: the stream is ours to close.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using WaveStream reader = Path.GetExtension(path).ToLowerInvariant() == ".wav"
            ? new WaveFileReader(file)
            : new AiffFileReader(file);
        var source = Plain(reader) ?? throw new InvalidDataException("Compressed WAV (" + reader.WaveFormat.Encoding + ").");
        return SampleReading.ToMono48k(source.ToSampleProvider(), reader.TotalTime.TotalSeconds, ct);
    }

    /// <summary>
    /// The stream as plain PCM or IEEE float: as it is, or a WAVE_FORMAT_EXTENSIBLE one (24-bit, float, 5.1…) read
    /// through the plain format its SubFormat names. Null for a compressed format (ADPCM, mu-law…): another decoder's job.
    /// </summary>
    private static IWaveProvider? Plain(WaveStream reader)
    {
        var f = reader.WaveFormat;
        if (f.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) return reader;
        if (f.Encoding != WaveFormatEncoding.Extensible || SubFormat(f) is not { } sub) return null;
        if (sub == PcmSubFormat && f.BitsPerSample is 8 or 16 or 24 or 32)
            return new AsFormat(reader, new WaveFormat(f.SampleRate, f.BitsPerSample, f.Channels));
        if (sub == FloatSubFormat && f.BitsPerSample == 32)
            return new AsFormat(reader, WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, f.Channels));
        return null;
    }

    /// <summary>
    /// WAVE_FORMAT_EXTENSIBLE's SubFormat GUID: NAudio's reader keeps it in the extra bytes after cbSize (valid bits 2,
    /// channel mask 4, then the 16-byte GUID); null when they are missing.
    /// </summary>
    private static Guid? SubFormat(WaveFormat f) => f switch
    {
        WaveFormatExtensible ext => ext.SubFormat,
        WaveFormatExtraData { ExtraData.Length: >= 22 } x => new Guid(x.ExtraData.AsSpan(6, 16)),
        _ => null,
    };

    /// <summary>The same bytes, described by another (equivalent) format.</summary>
    private sealed class AsFormat(IWaveProvider inner, WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = format;

        public int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    }
}

/// <summary>OGG Vorbis through NVorbis.</summary>
public sealed class OggDecoder : ISoundDecoder
{
    public string Name => "OGG (NVorbis)";

    public bool CanDecode(string extension) => extension == ".ogg";

    public float[] Decode(string path, CancellationToken ct)
    {
        // VorbisWaveReader leaves a path it could not decode open, so the stream is ours to close.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var ogg = new VorbisWaveReader(file, closeOnDispose: false);
        return SampleReading.ToMono48k(ogg, ogg.TotalTime.TotalSeconds, ct);
    }
}

/// <summary>MP3 through NLayer (managed, MIT).</summary>
public sealed class Mp3Decoder : ISoundDecoder
{
    public string Name => "MP3 (NLayer)";

    public bool CanDecode(string extension) => extension == ".mp3";

    public float[] Decode(string path, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var mpeg = new MpegFile(file);
        if (mpeg.SampleRate <= 0 || mpeg.Channels <= 0) throw new InvalidDataException("No MPEG audio frames.");
        return SampleReading.ToMono48k(new MpegSource(mpeg), mpeg.Duration.TotalSeconds, ct);
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
