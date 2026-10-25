using System;
using EasyVocal.Core.Engine;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EasyVocal.Platform.Windows;

/// <summary>
/// A microphone through WASAPI (shared mode, event-driven, 20 ms buffer). Whatever format the device uses is
/// turned into mono 48 kHz float by a <see cref="FormatConverter"/>. When the device is unplugged NAudio ends the
/// capture with an exception, which <see cref="Stopped"/> forwards.
/// </summary>
public sealed class WasapiMicInput : IAudioInput
{
    private static readonly Guid FloatSubType = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly FormatConverter converter;
    private float[] buffer = new float[4096];
    private bool disposed;

    /// <param name="deviceId">The endpoint id, or null for the Windows default microphone.</param>
    public WasapiMicInput(string? deviceId)
    {
        using (var enumerator = new MMDeviceEnumerator())
        {
            device = deviceId == null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : enumerator.GetDevice(deviceId);
        }
        try
        {
            capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
            var f = capture.WaveFormat;
            converter = new FormatConverter(f.SampleRate, f.Channels, f.BitsPerSample, IsFloat(f));
        }
        catch
        {
            device.Dispose();
            throw;
        }
        capture.DataAvailable += OnData;
        capture.RecordingStopped += OnStopped;
    }

    public event Action<float[], int>? Samples;
    public event Action<Exception?>? Stopped;

    public void Start() => capture.StartRecording();

    /// <summary>Shared-mode devices usually report WAVE_FORMAT_EXTENSIBLE; the sub-format tells float from integer.</summary>
    private static bool IsFloat(WaveFormat f) => f.Encoding switch
    {
        WaveFormatEncoding.IeeeFloat => true,
        WaveFormatEncoding.Extensible when f is WaveFormatExtensible x => x.SubFormat == FloatSubType,
        _ => false,
    };

    // Capture thread. An exception here ends the capture and comes back through RecordingStopped.
    private void OnData(object? sender, WaveInEventArgs e)
    {
        int needed = converter.MaxOutputSamples(e.BytesRecorded);
        if (buffer.Length < needed) buffer = new float[needed * 2]; // only when the device hands over a bigger chunk than ever
        int n = converter.Convert(e.Buffer, e.BytesRecorded, buffer);
        if (n > 0) Samples?.Invoke(buffer, n);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!disposed) Stopped?.Invoke(e.Exception);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try { capture.StopRecording(); } catch { /* already gone */ }
        capture.Dispose();
        device.Dispose();
    }
}
