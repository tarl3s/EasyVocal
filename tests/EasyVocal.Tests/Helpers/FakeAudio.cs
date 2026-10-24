using System;
using System.Collections.Generic;
using EasyVocal.Core.Engine;

namespace EasyVocal.Tests.Helpers;

/// <summary>A microphone the test drives by hand.</summary>
public sealed class FakeInput : IAudioInput
{
    public event Action<float[], int>? Samples;
    public event Action<Exception?>? Stopped;

    public int StartCount { get; private set; }
    public bool Disposed { get; private set; }
    public bool HasListeners => Samples != null;

    public void Start() => StartCount++;

    public void Dispose() => Disposed = true;

    /// <summary>Raises <see cref="Samples"/> over the signal in chunks of <paramref name="chunk"/> samples.</summary>
    public void Feed(float[] signal, int chunk = 480)
    {
        var buffer = new float[chunk];
        for (int i = 0; i < signal.Length; i += chunk)
        {
            int n = Math.Min(chunk, signal.Length - i);
            Array.Copy(signal, i, buffer, 0, n);
            Samples?.Invoke(buffer, n);
        }
    }

    public void RaiseStopped(Exception? error) => Stopped?.Invoke(error);
}

/// <summary>An output that records everything written to it.</summary>
public sealed class FakeOutput : IAudioOutput
{
    private readonly List<float> _written = new();

    public bool ThrowOnWrite { get; set; }
    public int BufferedSamples { get; set; }
    public int TrimCount { get; private set; }
    public int LastTrimTo { get; private set; } = -1;
    public int StartCount { get; private set; }
    public int WriteCount { get; private set; }
    public bool Disposed { get; private set; }

    public float[] Written => _written.ToArray();

    public void Start() => StartCount++;

    public void Write(ReadOnlySpan<float> mono)
    {
        if (ThrowOnWrite) throw new System.IO.IOException("device gone");
        WriteCount++;
        foreach (float v in mono) _written.Add(v);
    }

    public void TrimTo(int keepSamples)
    {
        TrimCount++;
        LastTrimTo = keepSamples;
        if (BufferedSamples > keepSamples) BufferedSamples = keepSamples;
    }

    public void Dispose() => Disposed = true;
}
