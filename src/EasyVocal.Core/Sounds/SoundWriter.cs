using System;
using System.IO;
using EasyVocal.Core.Dsp;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace EasyVocal.Core.Sounds;

/// <summary>Writes mono samples at <see cref="AudioFormat.SampleRate"/> to a WAV (16-bit) or MP3 file.</summary>
public static class SoundWriter
{
    private static readonly WaveFormat Pcm16 = new(AudioFormat.SampleRate, 16, 1);
    private static bool? mp3;

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

    /// <summary>Windows has an MP3 encoder (Media Foundation; missing on some "N" editions).</summary>
    public static bool Mp3Supported
    {
        get
        {
            if (mp3 is { } known) return known;
            bool ok;
            try
            {
                MediaFoundationApi.Startup();
                ok = MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_MP3, Pcm16, 128000) != null;
            }
            catch (Exception) { ok = false; }
            mp3 = ok;
            return ok;
        }
    }

    /// <summary>A 128 kbps MP3 through Windows' own encoder. Throws when there is none (check <see cref="Mp3Supported"/>).</summary>
    public static void WriteMp3(string path, float[] samples)
    {
        MediaFoundationApi.Startup();
        using var reader = new RawSourceWaveStream(new MemoryStream(ToPcm16(samples)), Pcm16);
        MediaFoundationEncoder.EncodeToMp3(reader, path, 128000);
    }

    private static byte[] ToPcm16(float[] samples)
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
