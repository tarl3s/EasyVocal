using System;
using System.Collections.Generic;
using EasyVocal.Core.Engine;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

public class FormatConverterTests
{
    private const float Amp = 0.5f;

    /// <summary>One second of a 440 Hz sine in every channel, encoded as WASAPI would deliver it.</summary>
    private static byte[] Encode(int sr, int ch, int bits, bool isFloat, double hz = 440, double seconds = 1)
    {
        int frames = (int)Math.Round(sr * seconds), bytesPer = bits / 8;
        var data = new byte[frames * ch * bytesPer];
        int p = 0;
        for (int n = 0; n < frames; n++)
        {
            double v = Amp * Math.Sin(2 * Math.PI * hz * n / sr);
            for (int c = 0; c < ch; c++)
            {
                if (isFloat)
                {
                    BitConverter.TryWriteBytes(data.AsSpan(p), (float)v);
                }
                else if (bits == 16)
                {
                    BitConverter.TryWriteBytes(data.AsSpan(p), (short)Math.Round(v * short.MaxValue));
                }
                else if (bits == 24)
                {
                    int s = (int)Math.Round(v * 8388607);
                    data[p] = (byte)s; data[p + 1] = (byte)(s >> 8); data[p + 2] = (byte)(s >> 16);
                }
                else
                {
                    BitConverter.TryWriteBytes(data.AsSpan(p), (int)Math.Round(v * int.MaxValue));
                }
                p += bytesPer;
            }
        }
        return data;
    }

    /// <summary>Feeds the bytes in chunks of <paramref name="chunkFrames"/> frames and collects the output.</summary>
    private static float[] Feed(FormatConverter conv, byte[] data, int frameBytes, int chunkFrames)
    {
        var all = new List<float>();
        var chunk = new byte[chunkFrames * frameBytes];
        var output = new float[conv.MaxOutputSamples(chunk.Length)];
        for (int at = 0; at < data.Length; at += chunk.Length)
        {
            int bytes = Math.Min(chunk.Length, data.Length - at);
            Array.Copy(data, at, chunk, 0, bytes);
            int n = conv.Convert(chunk, bytes, output);
            for (int i = 0; i < n; i++) all.Add(output[i]);
        }
        return all.ToArray();
    }

    [Theory]
    [InlineData(48000, 1, 16, false)]
    [InlineData(44100, 2, 16, false)]
    [InlineData(48000, 2, 32, true)]
    [InlineData(96000, 2, 24, false)]
    [InlineData(44100, 6, 32, true)]
    [InlineData(48000, 8, 32, false)]
    public void Convert_AnyFormat_ToMono48k(int sr, int ch, int bits, bool isFloat)
    {
        var conv = new FormatConverter(sr, ch, bits, isFloat);
        var data = Encode(sr, ch, bits, isFloat);
        var x = Feed(conv, data, ch * bits / 8, sr / 100); // 10 ms chunks

        Assert.InRange(x.Length, 48000 - 20, 48000 + 20);
        Assert.True(Signals.AllFinite(x));
        Assert.InRange(Signals.EstimatePitch(x), 435, 445);
        Assert.InRange(Signals.Peak(x), Amp * 0.9, Amp * 1.1);
    }

    [Fact]
    public void Convert_48kFloatMono_IsExactCopy()
    {
        var conv = new FormatConverter(48000, 1, 32, true);
        var data = Encode(48000, 1, 32, true, seconds: 0.1);
        var output = new float[conv.MaxOutputSamples(data.Length)];
        int n = conv.Convert(data, data.Length, output);
        Assert.Equal(4800, n);
        for (int i = 0; i < n; i++) Assert.Equal(BitConverter.ToSingle(data, i * 4), output[i]);
    }

    [Fact]
    public void Convert_OddChunkSizes_SameAsEvenChunks()
    {
        var data = Encode(44100, 2, 16, false, seconds: 0.5);
        var a = Feed(new FormatConverter(44100, 2, 16, false), data, 4, 441);
        var b = Feed(new FormatConverter(44100, 2, 16, false), data, 4, 37);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i], 1e-6f);
    }

    [Fact]
    public void Convert_96k_HighTonesDoNotFoldDown()
    {
        // 30 kHz cannot exist at 48 kHz: it must be filtered out, not alias to 18 kHz.
        var conv = new FormatConverter(96000, 1, 32, true);
        var x = Feed(conv, Encode(96000, 1, 32, true, hz: 30000, seconds: 0.5), 4, 960);
        Assert.True(Signals.Magnitude(Signals.Slice(x, 0.1, 0.5), 18000) < Amp * 0.1);
    }

    [Fact]
    public void Convert_OutputTooSmall_Throws()
    {
        var conv = new FormatConverter(48000, 1, 16, false);
        Assert.Throws<ArgumentException>(() => conv.Convert(new byte[960], 960, new float[100]));
    }

    [Theory]
    [InlineData(48000, 0, 16, false)]
    [InlineData(48000, 2, 8, false)]
    [InlineData(48000, 2, 16, true)]
    [InlineData(0, 2, 16, false)]
    public void Constructor_UnsupportedFormat_Throws(int sr, int ch, int bits, bool isFloat)
    {
        Assert.ThrowsAny<ArgumentException>(() => new FormatConverter(sr, ch, bits, isFloat));
    }
}
