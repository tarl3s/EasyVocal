using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Unix;

/// <summary>
/// One PortAudio stream with a callback (PortAudio's audio thread calls <see cref="OnAudio"/>; nothing there allocates
/// or locks). The backend's poll calls <see cref="Check"/>: a stream that stopped calling back (unplugged) fails.
/// </summary>
internal abstract unsafe class PortAudioStream : IDisposable
{
    /// <summary>No callback for this long while started: the device is gone.</summary>
    private static readonly long StallTicks = Stopwatch.Frequency * 3 / 2;

    protected readonly PortAudioBackend Backend;
    private readonly object gate = new(); // Check vs Dispose (never taken by the callback)
    private IntPtr stream;
    private GCHandle self;
    private long lastCallback;
    private volatile bool started;
    private int failed;
    protected volatile bool Disposed;

    protected PortAudioStream(PortAudioBackend backend) => Backend = backend;

    /// <summary>The device's name, for messages.</summary>
    protected string DeviceName { get; init; } = "";

    /// <summary>Under the backend's lock (the caller holds it): opens the stream; throws <see cref="PortAudioException"/>.</summary>
    protected void Open(PortAudioNative.StreamParameters* input, PortAudioNative.StreamParameters* output, double rate, int framesPerBuffer)
    {
        self = GCHandle.Alloc(this);
        int error = PortAudioNative.Pa_OpenStream(out stream, input, output, rate, new CULong((uint)framesPerBuffer),
            new CULong((uint)PortAudioNative.ClipOff), &Callback, (void*)GCHandle.ToIntPtr(self));
        if (error == PortAudioNative.NoError) return;
        self.Free();
        stream = IntPtr.Zero;
        throw new PortAudioException("Opening " + DeviceName, error);
    }

    /// <summary>Picks the first (channels, rate) the device takes. Null when none.</summary>
    protected static (int Channels, double Rate)? FirstSupported(int device, bool input, double latency, params (int Channels, double Rate)[] tries)
    {
        foreach (var (channels, rate) in tries)
        {
            if (channels <= 0) continue;
            var p = Parameters(device, channels, latency);
            int result = input ? PortAudioNative.Pa_IsFormatSupported(&p, null, rate) : PortAudioNative.Pa_IsFormatSupported(null, &p, rate);
            if (result == 0) return (channels, rate);
        }
        return null;
    }

    protected static PortAudioNative.StreamParameters Parameters(int device, int channels, double latency) => new()
    {
        Device = device,
        ChannelCount = channels,
        SampleFormat = new CULong((uint)PortAudioNative.Float32),
        SuggestedLatency = latency,
        HostApiSpecificStreamInfo = IntPtr.Zero,
    };

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Callback(void* input, void* output, CULong frames, void* timeInfo, CULong statusFlags, void* userData)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)userData).Target is not PortAudioStream s) return PortAudioNative.Complete;
            Volatile.Write(ref s.lastCallback, Stopwatch.GetTimestamp());
            if (s.Disposed) return PortAudioNative.Complete;
            s.OnAudio((float*)input, (float*)output, (int)frames.Value);
            return PortAudioNative.Continue;
        }
        catch (Exception)
        {
            return PortAudioNative.Continue; // never into PortAudio; a broken block is silence
        }
    }

    /// <summary>PortAudio's audio thread: <paramref name="input"/> (capture) or <paramref name="output"/> (playback), interleaved.</summary>
    protected abstract void OnAudio(float* input, float* output, int frames);

    /// <summary>The device stopped working (poll thread): report it once.</summary>
    protected abstract void Fail(Exception why);

    public void Start()
    {
        lock (Backend.Sync)
        {
            if (Disposed) throw new ObjectDisposedException(GetType().Name);
            Volatile.Write(ref lastCallback, Stopwatch.GetTimestamp());
            int error = PortAudioNative.Pa_StartStream(stream);
            if (error != PortAudioNative.NoError) throw new PortAudioException("Starting " + DeviceName, error);
            started = true;
        }
    }

    /// <summary>The backend's poll: fails a stream that stopped calling back or ended by itself; a refused microphone too.</summary>
    internal void Check(bool micRefused)
    {
        lock (gate)
        {
            if (Disposed || !started || Volatile.Read(ref failed) == 1) return;
            if (micRefused && this is PortAudioMic) { Report(new MicPermissionDeniedException()); return; }
            int active = PortAudioNative.Pa_IsStreamActive(stream);
            if (active < 0) { Report(new PortAudioException(DeviceName + " stopped", active)); return; }
            if (active == 0) { Report(new IOException(DeviceName + " stopped by itself.")); return; }
            if (Stopwatch.GetTimestamp() - Volatile.Read(ref lastCallback) > StallTicks)
                Report(new IOException(DeviceName + " stopped sending or taking sound (unplugged?)."));
        }
    }

    private void Report(Exception why)
    {
        if (Interlocked.Exchange(ref failed, 1) == 1) return;
        AppLog.Info("Sound device lost: " + why.Message);
        Fail(why);
    }

    /// <summary>For tests: the device failing as if unplugged.</summary>
    internal void SimulateFailure(Exception why) => Report(why);

    public void Dispose()
    {
        lock (gate)
        {
            if (Disposed) return;
            Disposed = true;
        }
        lock (Backend.Sync)
        {
            if (stream != IntPtr.Zero)
            {
                if (started) PortAudioNative.Pa_AbortStream(stream); // stop now; waits for a callback that is running
                PortAudioNative.Pa_CloseStream(stream);
                stream = IntPtr.Zero;
            }
            if (self.IsAllocated) self.Free();
        }
        Backend.Closed(this);
        OnDisposed();
    }

    protected virtual void OnDisposed() { }
}

/// <summary>
/// A microphone: mono at 48 kHz if the device takes it (else its own channels and rate, converted by
/// <see cref="FormatConverter"/>), 10 ms blocks. <see cref="Samples"/> is raised on PortAudio's audio thread.
/// </summary>
internal sealed unsafe class PortAudioMic : PortAudioStream, IAudioInput
{
    private readonly FormatConverter converter;
    private readonly int channels;
    private byte[] bytes;
    private float[] mono;

    public event Action<float[], int>? Samples;
    public event Action<Exception?>? Stopped;

    /// <summary>Under the backend's lock.</summary>
    public PortAudioMic(PortAudioBackend backend, PortAudioBackend.PaDevice device) : base(backend)
    {
        DeviceName = device.Name;
        double latency = Math.Max(device.LowInputLatency, 0.005);
        int stereo = Math.Min(2, device.MaxInputs);
        var format = FirstSupported(device.Index, input: true, latency,
            (1, AudioFormat.SampleRate), (stereo, AudioFormat.SampleRate), (1, device.DefaultRate), (stereo, device.DefaultRate))
            ?? throw new IOException(device.Name + " can't record in any format EasyVocal knows.");
        channels = format.Channels;
        int frames = Math.Max(1, (int)Math.Round(format.Rate / 100)); // 10 ms
        converter = new FormatConverter((int)Math.Round(format.Rate), channels, 32, isFloat: true);
        bytes = new byte[frames * channels * 4 * 2];
        mono = new float[converter.MaxOutputSamples(bytes.Length)];
        var p = Parameters(device.Index, channels, latency);
        Open(&p, null, format.Rate, frames);
        AppLog.Info("Microphone opened: " + device.Name + " (" + converter + ")");
    }

    protected override void OnAudio(float* input, float* output, int frames)
    {
        int length = frames * channels * 4;
        if (length > bytes.Length) return; // never: PortAudio gives the block size asked for
        fixed (byte* to = bytes)
        {
            if (input == null) new Span<byte>(to, length).Clear(); // an input underflow: silence
            else Buffer.MemoryCopy(input, to, bytes.Length, length);
        }
        int n = converter.Convert(bytes, length, mono);
        if (n > 0) Samples?.Invoke(mono, n);
    }

    protected override void Fail(Exception why) => Stopped?.Invoke(why);
}

/// <summary>Fills a span with the next 48 kHz samples.</summary>
internal delegate void SampleSource(Span<float> into);

/// <summary>
/// Mono 48 kHz pulled at another rate (a playback device that doesn't take 48 kHz): linear interpolation between the
/// samples it reads from <see cref="read"/>, its position carried from call to call. No allocation after it is made.
/// </summary>
internal sealed class RateConverter
{
    private readonly double step; // input samples per output sample
    private readonly SampleSource read;
    private readonly float[] chunk = new float[256];
    private int chunkAt, chunkLength;
    private float previous, next;
    private double position = 1; // in [0, 1): between previous and next; 1 = take a sample first

    /// <param name="read">Fills a span with the next 48 kHz samples (silence when there are none).</param>
    public RateConverter(double outputRate, SampleSource read)
    {
        step = AudioFormat.SampleRate / outputRate;
        this.read = read;
    }

    private float Take()
    {
        if (chunkAt == chunkLength)
        {
            read(chunk);
            chunkAt = 0;
            chunkLength = chunk.Length;
        }
        return chunk[chunkAt++];
    }

    public void Pull(Span<float> output)
    {
        for (int i = 0; i < output.Length; i++)
        {
            while (position >= 1)
            {
                previous = next;
                next = Take();
                position -= 1;
            }
            output[i] = previous + (float)position * (next - previous);
            position += step;
        }
    }
}

/// <summary>
/// A playback device fed mono 48 kHz: the samples wait in a <see cref="SampleQueue"/> (soft fades and silence when it
/// runs dry, like the WASAPI speaker) and are copied to every channel; a device that doesn't take 48 kHz plays at its
/// own rate through a <see cref="RateConverter"/>. A device that failed makes the next <see cref="Write"/> throw, which
/// the engine reports as a lost output.
/// </summary>
internal sealed unsafe class PortAudioSpeaker : PortAudioStream, IAudioOutput
{
    private const int CapacityMs = 500;
    private const int FadeSamples = AudioFormat.SampleRate * 5 / 1000;
    private const int PrimeSamples = AudioFormat.BlockSize;

    private readonly SampleQueue queue = new(AudioFormat.SampleRate * CapacityMs / 1000, FadeSamples, PrimeSamples);
    private readonly int channels;
    private readonly float[] mono = new float[8192];
    private readonly RateConverter? converter;
    private volatile Exception? failure;

    /// <summary>Under the backend's lock.</summary>
    public PortAudioSpeaker(PortAudioBackend backend, PortAudioBackend.PaDevice device) : base(backend)
    {
        DeviceName = device.Name;
        double latency = Math.Max(device.LowOutputLatency, 0.005);
        int stereo = Math.Min(2, device.MaxOutputs);
        var format = FirstSupported(device.Index, input: false, latency,
            (1, AudioFormat.SampleRate), (stereo, AudioFormat.SampleRate), (1, device.DefaultRate), (stereo, device.DefaultRate))
            ?? throw new IOException(device.Name + " can't play in any format EasyVocal knows.");
        channels = format.Channels;
        if (Math.Abs(format.Rate - AudioFormat.SampleRate) > 0.5) converter = new RateConverter(format.Rate, span => queue.Read(span));
        var p = Parameters(device.Index, channels, latency);
        Open(null, &p, format.Rate, Math.Max(1, (int)Math.Round(format.Rate / 100))); // 10 ms
        AppLog.Info("Playback device opened: " + device.Name + " (" + channels + " ch, " + format.Rate + " Hz)");
    }

    public int BufferedSamples => queue.Count;

    public void Write(ReadOnlySpan<float> samples)
    {
        var why = failure;
        if (why != null) throw new IOException("The audio output stopped.", why);
        if (Disposed) throw new ObjectDisposedException(nameof(PortAudioSpeaker));
        queue.Write(samples);
    }

    public void TrimTo(int keepSamples) => queue.TrimTo(keepSamples);

    protected override void OnAudio(float* input, float* output, int frames)
    {
        if (output == null) return;
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(frames - done, mono.Length);
            var block = mono.AsSpan(0, n);
            if (converter != null) converter.Pull(block);
            else queue.Read(block);
            float* to = output + done * channels;
            for (int i = 0; i < n; i++)
            {
                float v = block[i];
                for (int c = 0; c < channels; c++) to[i * channels + c] = v;
            }
            done += n;
        }
    }

    protected override void Fail(Exception why) => failure = why;
}
