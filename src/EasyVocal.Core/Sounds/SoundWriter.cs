using System;
using System.IO;
using EasyVocal.Core.Dsp;
using NAudio.Wave;

namespace EasyVocal.Core.Sounds;

/// <summary>Writes mono samples at <see cref="AudioFormat.SampleRate"/> as an MP3 file (a platform's own encoder).</summary>
public interface IMp3Encoder
{
    /// <summary>The encoder is there (Windows' Media Foundation one is missing on some "N" editions).</summary>
    bool Supported { get; }

    /// <summary>A 128 kbps MP3. Throws when it can't.</summary>
    void Write(string path, float[] samples);
}

/// <summary>Writes mono samples at <see cref="AudioFormat.SampleRate"/> to a WAV (16-bit) or MP3 file.</summary>
public static class SoundWriter
{
    /// <summary>The 16-bit mono 48 kHz format of every file EasyVocal writes.</summary>
    public static readonly WaveFormat Pcm16 = new(AudioFormat.SampleRate, 16, 1);

    /// <summary>This platform's MP3 encoder; null = MP3 export is not offered (the Windows platform sets it).</summary>
    public static IMp3Encoder? Mp3Encoder { get; set; }

    /// <summary>A 16-bit WAV; samples are limited to -1..1 (anything louder is clipped).</summary>
    public static void WriteWav(string path, float[] samples)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteWav(stream, samples);
    }

    /// <summary>A 16-bit WAV into <paramref name="stream"/> (closed afterwards).</summary>
    public static void WriteWav(Stream stream, float[] samples)
    {
        using var writer = new WaveFileWriter(stream, Pcm16);
        var bytes = ToPcm16(samples);
        writer.Write(bytes, 0, bytes.Length);
    }

    /// <summary>This computer can write MP3 files.</summary>
    public static bool Mp3Supported => Mp3Encoder?.Supported == true;

    /// <summary>A 128 kbps MP3 through the platform's encoder. Throws when there is none (check <see cref="Mp3Supported"/>).</summary>
    public static void WriteMp3(string path, float[] samples)
    {
        var encoder = Mp3Encoder;
        if (encoder is not { Supported: true }) throw new NotSupportedException("This computer has no MP3 encoder.");
        encoder.Write(path, samples);
    }

    /// <summary>16-bit little-endian PCM, clipped to -1..1 (NaN and infinities become silence).</summary>
    public static byte[] ToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            if (!float.IsFinite(v)) v = 0;
            short s = (short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f);
            bytes[2 * i] = (byte)s;
            bytes[2 * i + 1] = (byte)(s >> 8);
        }
        return bytes;
    }
}
