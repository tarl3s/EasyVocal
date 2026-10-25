using System;
using System.IO;
using EasyVocal.Core.Dsp;

namespace EasyVocal.Tests.Helpers;

/// <summary>Just enough WAV for the voice tests: 16-bit PCM or 32-bit float in (any rate, mixed to mono at 48 kHz), 16-bit out.</summary>
public static class WavFile
{
    private const int Sr = AudioFormat.SampleRate;

    public static float[] ReadMono(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        r.ReadBytes(12);
        int channels = 1, rate = Sr, bits = 16, format = 1;
        while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
        {
            string id = new string(r.ReadChars(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32();
                r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                r.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                int frames = size / (bits / 8) / channels;
                var mono = new float[frames];
                for (int n = 0; n < frames; n++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += format == 3 ? r.ReadSingle() : bits == 16 ? r.ReadInt16() / 32768f : throw new NotSupportedException("use a 16-bit or float WAV");
                    mono[n] = sum / channels;
                }
                return rate == Sr ? mono : Resample(mono, rate);
            }
            else r.ReadBytes(size + (size & 1));
        }
        throw new InvalidDataException("no data chunk");
    }

    private static float[] Resample(float[] x, int rate)
    {
        var o = new float[(int)((long)x.Length * Sr / rate)];
        for (int n = 0; n < o.Length; n++)
        {
            double p = (double)n * rate / Sr;
            int i = (int)p;
            float f = (float)(p - i);
            o[n] = x[i] + (x[Math.Min(i + 1, x.Length - 1)] - x[i]) * f;
        }
        return o;
    }

    /// <summary>16-bit PCM mono at 48 kHz (clipped to ±1), so any player opens it.</summary>
    public static void WriteMono(string path, float[] x)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF".ToCharArray()); w.Write(36 + x.Length * 2); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Sr);
        w.Write(Sr * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(x.Length * 2);
        foreach (float v in x) w.Write((short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767));
    }
}
