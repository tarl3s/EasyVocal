using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Unix;

/// <summary>
/// What PortAudio's audio thread is handed with every block (the stream's userData), in native memory: the stream
/// itself (a GCHandle), how many channels to silence, and whether PortAudio said the stream finished.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PaCallbackContext
{
    public IntPtr Self;
    public int OutputChannels;
    public int Finished;
}

/// <summary>
/// One PortAudio stream with a callback (PortAudio's audio thread calls <see cref="OnAudio"/>; nothing there allocates
/// or locks). The backend's poll calls <see cref="Check"/>: a stream that stopped calling back (unplugged) fails.
/// <para>
/// Closing (<see cref="Dispose"/>, which never waits): from then on the callback gives silence and answers paComplete,
/// so PortAudio stops the stream on its own audio thread; a thread of its own waits for PortAudio to say that stop is
/// over, and only then makes the stream "stopped" (Pa_AbortStream, with nothing left to stop) and closes it. Stopping a
/// stream from outside while its audio thread stops it deadlocks on macOS — both end in AudioOutputUnitStop and wait for
/// each other (EasyVocal froze) — and Pa_CloseStream on a stream that isn't "stopped" aborts it, which macOS's PortAudio
/// never marks by itself after paComplete. Only a stream whose callback no longer runs (a device gone) is aborted from
/// outside, and its callback answers paContinue from that moment, so it is never stopped from two threads.
/// </para>
/// </summary>
internal abstract unsafe class PortAudioStream : IDisposable
{
    /// <summary>No callback for this long while started: the device is gone.</summary>
    private static readonly long StallTicks = Stopwatch.Frequency * 3 / 2;

    private const int Running = 0, Closing = 1, Completing = 2, Aborting = 3; // closeState

    protected readonly PortAudioBackend Backend;
    private readonly object gate = new(); // Check vs Dispose (never taken by the callback)
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IPaStreamCalls calls = PaStreamCalls.Instance;
    private IntPtr stream;
    private GCHandle self;
    private PaCallbackContext* context;
    private long lastCallback;
    private volatile bool started;
    private int failed, closeState = Running;
    protected volatile bool Disposed;

    protected PortAudioStream(PortAudioBackend backend) => Backend = backend;

    /// <summary>The device's name, for messages.</summary>
    protected string DeviceName { get; init; } = "";

    /// <summary>How long a closing stream's callback has to answer (a live one does within a block, 10 ms).</summary>
    internal TimeSpan AnswerWait { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long PortAudio has to say that a stream whose callback completed has stopped.</summary>
    internal TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Done when the stream is closed (or left open on purpose, see <see cref="CloseRoute"/>).</summary>
    internal Task Closed => closed.Task;

    /// <summary>
    /// How the stream ended, for the tests: "completed" (stopped by its own audio thread), "aborted" (no callback any
    /// more), "never started", "left open"; null while open or closing.
    /// </summary>
    internal string? CloseRoute { get; private set; }

    /// <summary>Under the backend's lock (the caller holds it): opens the stream; throws <see cref="PortAudioException"/>.</summary>
    protected void Open(PortAudioNative.StreamParameters* input, PortAudioNative.StreamParameters* output, double rate, int framesPerBuffer)
    {
        Attach(output != null ? output->ChannelCount : 0);
        int error = PortAudioNative.Pa_OpenStream(out stream, input, output, rate, new CULong((uint)framesPerBuffer),
            new CULong((uint)PortAudioNative.ClipOff), &Callback, context);
        if (error != PortAudioNative.NoError)
        {
            stream = IntPtr.Zero;
            FreeContext();
            throw new PortAudioException("Opening " + DeviceName, error);
        }
        error = PortAudioNative.Pa_SetStreamFinishedCallback(stream, &Finished);
        if (error != PortAudioNative.NoError) AppLog.Info("PortAudio won't say when " + DeviceName + " stops (" + error + "): closing it waits longer");
    }

    private void Attach(int outputChannels)
    {
        self = GCHandle.Alloc(this);
        context = (PaCallbackContext*)NativeMemory.AllocZeroed((nuint)sizeof(PaCallbackContext));
        context->Self = GCHandle.ToIntPtr(self);
        context->OutputChannels = outputChannels;
    }

    /// <summary>Only once nothing can call back any more (the stream is closed or was never opened).</summary>
    private void FreeContext()
    {
        if (context != null)
        {
            NativeMemory.Free(context);
            context = null;
        }
        if (self.IsAllocated) self.Free();
    }

    /// <summary>For tests: a pretend stream on <paramref name="pretend"/> calls (no PortAudio, no device).</summary>
    internal void AttachForTests(IPaStreamCalls pretend, int outputChannels)
    {
        calls = pretend;
        Attach(outputChannels);
        stream = 1;
    }

    /// <summary>For tests: one block as PortAudio's audio thread hands it over; returns the callback's answer.</summary>
    internal int CallbackForTests(float[] output, int frames)
    {
        fixed (float* o = output) return Answer(context, null, o, (nuint)frames);
    }

    /// <summary>For tests: PortAudio says the stream finished.</summary>
    internal void FinishedForTests() => Volatile.Write(ref context->Finished, 1);

    /// <summary>For tests: the callback with a userData whose handle is <paramref name="target"/> (zero = none).</summary>
    internal static int CallbackForTests(IntPtr target, int outputChannels, float[] output, int frames)
    {
        var ctx = new PaCallbackContext { Self = target, OutputChannels = outputChannels };
        fixed (float* o = output) return Answer(&ctx, null, o, (nuint)frames);
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
    private static int Callback(void* input, void* output, CULong frames, void* timeInfo, CULong statusFlags, void* userData) =>
        Answer((PaCallbackContext*)userData, input, output, frames.Value);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Finished(void* userData)
    {
        if (userData != null) Volatile.Write(ref ((PaCallbackContext*)userData)->Finished, 1);
    }

    /// <summary>
    /// PortAudio's audio thread, every block. A closing stream gives silence and answers paComplete (paContinue once it
    /// is being aborted from outside); a userData that isn't a stream of ours gives silence and paComplete.
    /// </summary>
    private static int Answer(PaCallbackContext* ctx, void* input, void* output, nuint frames)
    {
        if (ctx == null) return PortAudioNative.Complete;
        try
        {
            if (ctx->Self == IntPtr.Zero || GCHandle.FromIntPtr(ctx->Self).Target is not PortAudioStream s)
            {
                Silence(ctx, output, frames);
                return PortAudioNative.Complete;
            }
            Volatile.Write(ref s.lastCallback, Stopwatch.GetTimestamp());
            if (s.Disposed)
            {
                Silence(ctx, output, frames);
                return Interlocked.CompareExchange(ref s.closeState, Completing, Closing) == Aborting ? PortAudioNative.Continue : PortAudioNative.Complete;
            }
            s.OnAudio((float*)input, (float*)output, (int)frames);
            return PortAudioNative.Continue;
        }
        catch (Exception)
        {
            Silence(ctx, output, frames); // never into PortAudio; a broken block is silence
            return PortAudioNative.Continue;
        }
    }

    private static void Silence(PaCallbackContext* ctx, void* output, nuint frames)
    {
        if (output != null && ctx->OutputChannels > 0) new Span<float>(output, (int)frames * ctx->OutputChannels).Clear();
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
            int error = calls.Start(stream);
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
            int active = calls.IsActive(stream);
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

    /// <summary>Closes the stream on a thread of its own (see the class): never waits, not even for the backend's lock.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (Disposed) return;
            Volatile.Write(ref closeState, Closing);
            Disposed = true;
        }
        Backend.Closing(this);
        new Thread(CloseWhenStopped) { IsBackground = true, Name = "EasyVocal: closing a sound stream" }.Start();
    }

    private bool IsFinished => Volatile.Read(ref context->Finished) == 1;

    /// <summary>Waits in 2 ms steps until <paramref name="done"/> or <paramref name="limit"/>; true when done.</summary>
    private static bool WaitFor(Func<bool> done, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed >= limit) return false;
            Thread.Sleep(2);
        }
        return true;
    }

    /// <summary>The closing thread (see the class).</summary>
    private void CloseWhenStopped()
    {
        string route = "completed";
        bool leftOpen = false;
        try
        {
            lock (Backend.Sync)
            {
                // Start checks Disposed under this lock: a stream not started by now never will be (no audio thread).
                if (!started)
                {
                    route = "never started";
                    CloseNow();
                    return;
                }
            }
            // 1. Within a block the callback answers paComplete (or the stream already ended by itself).
            WaitFor(() => Volatile.Read(ref closeState) != Closing || IsFinished, AnswerWait);
            if (!IsFinished && Interlocked.CompareExchange(ref closeState, Aborting, Closing) == Closing)
            {
                // 2a. No callback for that long (a device gone): it answers paContinue from now on, so this is the only stop.
                route = "aborted";
                lock (Backend.Sync) calls.Abort(stream);
            }
            else if (!WaitFor(() => IsFinished, StopWait) && calls.IsActive(stream) != 0)
            {
                // 2b. Its audio thread stops it, and PortAudio didn't say so in time (inactive by now: nothing is stopping
                // it any more). Still active, which it never should be after paComplete: stopping it from here could
                // freeze the sound system, so it stays open.
                route = "left open";
                leftOpen = true;
                AppLog.Info(DeviceName + " didn't stop: left open");
                return;
            }
            // 3. Nothing runs on it now: made "stopped" (Pa_CloseStream would abort a stream that isn't), then closed.
            lock (Backend.Sync)
            {
                if (calls.IsStopped(stream) != 1) calls.Abort(stream);
                CloseNow();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Closing " + DeviceName + " failed", ex);
        }
        finally
        {
            Backend.Closed(this, stillOpen: leftOpen); // one left open stays counted: PortAudio is never restarted under it
            CloseRoute = route;
            closed.TrySetResult();
        }
    }

    /// <summary>Under the backend's lock, with nothing running on the stream: closes it and frees what its callback used.</summary>
    private void CloseNow()
    {
        if (stream != IntPtr.Zero) calls.Close(stream);
        stream = IntPtr.Zero;
        FreeContext();
    }
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
