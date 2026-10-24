using System;
using System.Buffers.Binary;
using EasyVocal.Core.Dsp;

namespace EasyVocal.Core.Engine;

/// <summary>
/// Turns what a WASAPI capture device delivers (16/24/32-bit integer or 32-bit float, any channel count, any rate)
/// into the engine's mono float at <see cref="AudioFormat.SampleRate"/>.
/// <para>
/// Channels are averaged. A 48 kHz device passes through untouched; any other rate goes through a streaming
/// Catmull-Rom resampler whose state (and the low-pass in front of it when the device is faster than 48 kHz)
/// carries over between calls, so chunk sizes never matter. <see cref="Convert"/> never allocates.
/// Only whole frames are read: WASAPI never splits one.
/// </para>
/// </summary>
public sealed class FormatConverter
{
    private const int OutRate = AudioFormat.SampleRate;

    /// <summary>Q of the four sections of an 8th-order Butterworth low-pass.</summary>
    private static readonly double[] ButterworthQ = { 0.5098, 0.6013, 0.9000, 2.5629 };

    private enum SampleKind { Int16, Int24, Int32, Float32 }

    private readonly int channels;
    private readonly int bytesPerSample;
    private readonly int frameBytes;
    private readonly SampleKind kind;
    private readonly float channelScale;
    private readonly int sampleRate;
    private readonly bool passThrough;
    private readonly double step;          // input samples per output sample
    private readonly Biquad[] antiAlias;   // only when downsampling

    // Resampler state: the last four input samples and where the next output falls between h1 and h2.
    private float h0, h1, h2, h3;
    private double pos;

    public FormatConverter(int sampleRate, int channels, int bitsPerSample, bool isFloat)
    {
        if (sampleRate < 8000 || sampleRate > 768000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels < 1 || channels > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        kind = (isFloat, bitsPerSample) switch
        {
            (true, 32) => SampleKind.Float32,
            (false, 16) => SampleKind.Int16,
            (false, 24) => SampleKind.Int24,
            (false, 32) => SampleKind.Int32,
            _ => throw new ArgumentException($"Unsupported sample format: {bitsPerSample}-bit {(isFloat ? "float" : "integer")}.", nameof(bitsPerSample)),
        };
        this.sampleRate = sampleRate;
        this.channels = channels;
        bytesPerSample = bitsPerSample / 8;
        frameBytes = bytesPerSample * channels;
        channelScale = 1f / channels;
        passThrough = sampleRate == OutRate;
        step = (double)sampleRate / OutRate;

        antiAlias = Array.Empty<Biquad>();
        if (sampleRate > OutRate)
        {
            antiAlias = new Biquad[ButterworthQ.Length];
            for (int i = 0; i < antiAlias.Length; i++) antiAlias[i] = Biquad.LowPass(sampleRate, 20000, ButterworthQ[i]);
        }
    }

    /// <summary>The most samples <see cref="Convert"/> can return for <paramref name="bytes"/> of input: size the output buffer with it.</summary>
    public int MaxOutputSamples(int bytes)
    {
        int frames = Math.Max(0, bytes) / frameBytes;
        return passThrough ? frames : (int)Math.Ceiling(frames / step) + 2;
    }

    /// <summary>Converts the first <paramref name="bytes"/> of <paramref name="data"/>; returns how many mono 48 kHz samples went into <paramref name="output"/>.</summary>
    public int Convert(byte[] data, int bytes, float[] output)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(output);
        if (bytes < 0 || bytes > data.Length) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (output.Length < MaxOutputSamples(bytes))
            throw new ArgumentException("The output buffer is too small; size it with MaxOutputSamples.", nameof(output));

        var src = data.AsSpan(0, bytes);
        int frames = bytes / frameBytes, o = 0;
        for (int f = 0, at = 0; f < frames; f++, at += frameBytes)
        {
            float sum = 0;
            for (int c = 0, p = at; c < channels; c++, p += bytesPerSample) sum += Read(src, p);
            float mono = channels == 1 ? sum : sum * channelScale;

            if (passThrough)
            {
                output[o++] = mono;
                continue;
            }
            for (int i = 0; i < antiAlias.Length; i++) mono = antiAlias[i].Process(mono);

            h0 = h1; h1 = h2; h2 = h3; h3 = mono;
            while (pos < 1.0)
            {
                float t = (float)pos;
                output[o++] = h1 + 0.5f * t * (h2 - h0 + t * (2f * h0 - 5f * h1 + 4f * h2 - h3 + t * (3f * (h1 - h2) + h3 - h0)));
                pos += step;
            }
            pos -= 1.0;
        }
        return o;
    }

    private float Read(ReadOnlySpan<byte> s, int p) => kind switch
    {
        SampleKind.Float32 => Finite(BinaryPrimitives.ReadSingleLittleEndian(s.Slice(p))),
        SampleKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(s.Slice(p)) * (1f / 32768f),
        SampleKind.Int24 => (s[p] | (s[p + 1] << 8) | ((sbyte)s[p + 2] << 16)) * (1f / 8388608f),
        _ => BinaryPrimitives.ReadInt32LittleEndian(s.Slice(p)) * (1f / 2147483648f),
    };

    private static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    public override string ToString() => $"{sampleRate} Hz, {channels} ch, {kind} → mono {OutRate} Hz";
}
