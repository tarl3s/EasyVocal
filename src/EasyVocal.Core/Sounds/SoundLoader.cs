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

/// <summary>Reads a WAV, MP3 or OGG file into mono samples at <see cref="AudioFormat.SampleRate"/>.</summary>
public static class SoundLoader
{
    /// <summary>Longest sound we accept, in seconds.</summary>
    public const double MaxSeconds = 600;

    public static readonly string[] Extensions = { ".wav", ".mp3", ".ogg" };

    public static float[] Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new SoundLoadException("We couldn't find that sound file.");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(Extensions, ext) < 0)
            throw new SoundLoadException("That kind of file isn't supported. Please pick a WAV, MP3 or OGG file.");

        try
        {
            using WaveStream reader = ext == ".ogg" ? OpenOgg(path) : new AudioFileReader(path);
            if (reader.TotalTime.TotalSeconds > MaxSeconds)
                throw TooLong();
            return ReadMono(reader, (ISampleProvider)reader);
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
