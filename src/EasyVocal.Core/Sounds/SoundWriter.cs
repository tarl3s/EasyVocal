using System;
using System.IO;
using EasyVocal.Core.Dsp;
using NAudio.Wave;

namespace EasyVocal.Core.Sounds;

/// <summary>
/// Writes mono samples at <see cref="AudioFormat.SampleRate"/> as an MP3 file: a platform's own encoder, handed out as
/// <c>PlatformServices.Mp3Encoder</c> (null where there is none: MP3 export is then not offered).
/// </summary>
public interface IMp3Encoder
{
    /// <summary>The encoder is there (Windows' Media Foundation one is missing on some "N" editions).</summary>
    bool Supported { get; }

    /// <summary>A 128 kbps MP3. Throws when it can't.</summary>
    void Write(string path, float[] samples);
}

/// <summary>Writes mono samples at <see cref="AudioFormat.SampleRate"/> to a 16-bit WAV (MP3: the platform's <see cref="IMp3Encoder"/>).</summary>
public static class SoundWriter
{
    /// <summary>The 16-bit mono 48 kHz format of every file EasyVocal writes.</summary>
    public static readonly WaveFormat Pcm16 = new(AudioFormat.SampleRate, 16, 1);

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
