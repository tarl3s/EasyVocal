using System;
using System.IO;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EasyVocal.Platform.Windows;

/// <summary>
/// A playback device through WASAPI (shared mode, event sync, 20 ms latency) fed mono 48 kHz float. The samples
/// wait in a <see cref="SampleQueue"/> (soft fades and silence when it runs dry) and are spread to the device's
/// channels: both sides on stereo, front left/right on surround. A device that stops by itself (unplugged,
/// disabled) makes the next <see cref="Write"/> throw, which the engine reports as a lost output.
/// </summary>
public sealed class WasapiSpeakerOutput : IAudioOutput
{
    private const int LatencyMs = 20;
    private const int CapacityMs = 500;
    /// <summary>Crossfade at a trim and fades around a dry spell (5 ms).</summary>
    private const int FadeSamples = AudioFormat.SampleRate * 5 / 1000;
    /// <summary>After running dry, playback waits for one block (10 ms) so it doesn't stutter.</summary>
    private const int PrimeSamples = AudioFormat.BlockSize;

    private readonly MMDevice device;
    private readonly WasapiOut output;
    private readonly SampleQueue queue = new(AudioFormat.SampleRate * CapacityMs / 1000, FadeSamples, PrimeSamples);
    private volatile Exception? failure;
    private volatile bool disposed;

    /// <param name="deviceId">The endpoint id, or null for the Windows default playback device.</param>
    public WasapiSpeakerOutput(string? deviceId)
    {
        using (var enumerator = new MMDeviceEnumerator())
        {
            device = deviceId == null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                : enumerator.GetDevice(deviceId);
        }
        try
        {
            int channels = device.AudioClient.MixFormat.Channels;
            ISampleProvider samples = new QueueProvider(queue);
            if (channels == 2) samples = samples.ToStereo();
            else if (channels > 2) samples = new MonoToFrontPair(samples, channels);

            output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: LatencyMs);
            output.PlaybackStopped += OnStopped;
            output.Init(samples);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public int BufferedSamples => queue.Count;

    public void Start() => output.Play();

    /// <summary>Capture thread: queues the samples.</summary>
    public void Write(ReadOnlySpan<float> mono)
    {
        var why = failure;
        if (why != null) throw new IOException("The audio output stopped.", why);
        if (disposed) throw new ObjectDisposedException(nameof(WasapiSpeakerOutput));
        queue.Write(mono);
    }

    public void TrimTo(int keepSamples) => queue.TrimTo(keepSamples);

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!disposed) failure = e.Exception ?? new IOException("Playback stopped by itself.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        output.PlaybackStopped -= OnStopped;
        try { output.Stop(); } catch { /* already gone */ }
        output.Dispose();
        device.Dispose();
    }

    /// <summary>The queue as an NAudio source: always a full read (silence when dry; WASAPI would stop on a short read).</summary>
    private sealed class QueueProvider : ISampleProvider
    {
        private readonly SampleQueue queue;

        public QueueProvider(SampleQueue queue) => this.queue = queue;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            queue.Read(buffer.AsSpan(offset, count));
            return count;
        }
    }

    /// <summary>Mono to the first two channels of a surround device (the others stay silent).</summary>
    private sealed class MonoToFrontPair : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int channels;
        private float[] mono = new float[1024];

        public MonoToFrontPair(ISampleProvider source, int channels)
        {
            this.source = source;
            this.channels = channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        // Render thread: grows its buffer only if the device ever asks for more than before.
        public int Read(float[] dest, int offset, int count)
        {
            int frames = count / channels;
            if (mono.Length < frames) mono = new float[frames];
            int got = source.Read(mono, 0, frames);
            for (int f = 0, o = offset; f < got; f++, o += channels)
            {
                dest[o] = dest[o + 1] = mono[f];
                for (int c = 2; c < channels; c++) dest[o + c] = 0f;
            }
            return got * channels;
        }
    }
}
