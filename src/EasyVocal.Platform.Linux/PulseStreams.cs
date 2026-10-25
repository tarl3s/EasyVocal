using System.Runtime.InteropServices;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;

namespace EasyVocal.Platform.Linux;

/// <summary>
/// libpulse-simple (part of libpulse0, on every desktop with PulseAudio or PipeWire's pipewire-pulse): a blocking stream
/// to or from one named sink/source. Mono float32 at 48 kHz; the server converts to the device.
/// </summary>
internal static unsafe class PulseSimple
{
    public const string Lib = "libpulse-simple.so.0";
    private const string Pulse = "libpulse.so.0";

    public const int Playback = 1, Record = 2;
    private const int Float32Le = 5;

    /// <summary>How much a playback stream keeps queued at the server (30 ms): enough against dropouts, little delay.</summary>
    public const int PlaybackBlocks = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferAttr
    {
        public uint MaxLength, TLength, PreBuf, MinReq, FragSize;
    }

    [DllImport(Lib)]
    private static extern IntPtr pa_simple_new(string? server, string name, int direction, string? device, string streamName,
        SampleSpec* spec, IntPtr channelMap, BufferAttr* attr, int* error);

    [DllImport(Lib)] public static extern int pa_simple_read(IntPtr s, void* data, nuint bytes, int* error);
    [DllImport(Lib)] public static extern int pa_simple_write(IntPtr s, void* data, nuint bytes, int* error);
    [DllImport(Lib)] public static extern void pa_simple_free(IntPtr s);
    [DllImport(Pulse)] private static extern IntPtr pa_strerror(int error);

    /// <summary>The buffer sizes asked for (bytes): recording in blocks of <paramref name="blockBytes"/>; playback ~30 ms queued.</summary>
    public static BufferAttr Attributes(int direction, int blockBytes) => direction == Record
        ? new BufferAttr { MaxLength = uint.MaxValue, TLength = uint.MaxValue, PreBuf = uint.MaxValue, MinReq = uint.MaxValue, FragSize = (uint)blockBytes }
        : new BufferAttr { MaxLength = uint.MaxValue, TLength = (uint)(PlaybackBlocks * blockBytes), PreBuf = uint.MaxValue, MinReq = (uint)blockBytes, FragSize = uint.MaxValue };

    /// <summary>A stream on <paramref name="device"/> (a sink for playback, a source for recording). Throws <see cref="IOException"/>.</summary>
    public static IntPtr Open(int direction, string device, string streamName, int blockBytes)
    {
        var spec = new SampleSpec { Format = Float32Le, Rate = AudioFormat.SampleRate, Channels = 1 };
        var attr = Attributes(direction, blockBytes);
        int error = 0;
        var s = pa_simple_new(null, "EasyVocal", direction, device, streamName, &spec, IntPtr.Zero, &attr, &error);
        if (s == IntPtr.Zero) throw new IOException("Opening " + device + ": " + ErrorText(error));
        return s;
    }

    /// <summary>One blocking read that fills <paramref name="buffer"/>: 0, or below 0 on failure (<paramref name="error"/>).</summary>
    public static int Read(IntPtr s, float[] buffer, out int error)
    {
        int e = 0, r;
        fixed (float* p = buffer) r = pa_simple_read(s, p, (nuint)(buffer.Length * sizeof(float)), &e);
        error = e;
        return r;
    }

    public static string ErrorText(int error)
    {
        try { return Marshal.PtrToStringUTF8(pa_strerror(error)) ?? ("PulseAudio error " + error); }
        catch (Exception) { return "PulseAudio error " + error; }
    }
}

/// <summary>
/// One open Pulse stream as the backend sees it. A stream follows its device by name only while that device exists: the
/// server would otherwise move it to another device (the voice into the speakers), so the backend fails it when its
/// device leaves the list.
/// </summary>
internal interface IPulseStream
{
    string Device { get; }
    bool IsInput { get; }

    /// <summary>The device went away (any thread): report it once and stop using the stream.</summary>
    void Fail(Exception why);
}

/// <summary>A source read on a thread of its own, 10 ms at a time. <see cref="Samples"/> is raised on that thread.</summary>
internal sealed unsafe class PulseMic : IAudioInput, IPulseStream
{
    private const int Block = AudioFormat.BlockSize;
    private readonly PulseBackend? backend;
    private readonly float[] buffer = new float[Block];
    private IntPtr stream;
    private Thread? thread;
    private volatile bool stop;
    private int disposed, reported;

    public event Action<float[], int>? Samples;
    public event Action<Exception?>? Stopped;

    /// <param name="backend">Told when this closes (null: a test's stream of its own).</param>
    public PulseMic(PulseBackend? backend, string device)
    {
        this.backend = backend;
        Device = device;
        stream = PulseSimple.Open(PulseSimple.Record, device, "Microphone", Block * sizeof(float));
        AppLog.Info("Microphone opened: " + device + " (PulseAudio, mono 48000 Hz)");
    }

    public string Device { get; }
    public bool IsInput => true;

    public void Start()
    {
        if (thread != null) return;
        thread = new Thread(Run) { IsBackground = true, Name = "EasyVocal mic", Priority = ThreadPriority.Highest };
        thread.Start();
    }

    private void Run()
    {
        Exception? why = null;
        try
        {
            while (!stop)
            {
                int error = 0;
                fixed (float* p = buffer)
                    if (PulseSimple.pa_simple_read(stream, p, (nuint)(Block * sizeof(float)), &error) < 0)
                    {
                        why = new IOException(Device + " stopped: " + PulseSimple.ErrorText(error));
                        break;
                    }
                if (!stop) Samples?.Invoke(buffer, Block);
            }
        }
        catch (Exception ex) { why = ex; }
        if (!stop) Fail(why ?? new IOException(Device + " stopped."));
    }

    public void Fail(Exception why)
    {
        if (Interlocked.Exchange(ref reported, 1) == 1) return;
        stop = true; // the thread stops reading (a moved stream would hear another device)
        AppLog.Info("Microphone lost: " + why.Message);
        Stopped?.Invoke(why);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        Interlocked.Exchange(ref reported, 1); // closing is not a failure
        stop = true;
        // A read returns within a block (10 ms); a stream whose device vanished returns an error at once.
        if (thread != null && !thread.Join(TimeSpan.FromSeconds(2))) AppLog.Info("The microphone thread didn't stop in time: " + Device);
        if (thread == null || !thread.IsAlive) PulseSimple.pa_simple_free(stream);
        stream = IntPtr.Zero;
        backend?.Closed(this);
    }
}

/// <summary>
/// A sink fed mono 48 kHz from a <see cref="SampleQueue"/> by a thread of its own (silence when the queue runs dry,
/// so the stream never underruns and its latency stays put). A sink that failed makes the next <see cref="Write"/> throw.
/// </summary>
internal sealed unsafe class PulseSpeaker : IAudioOutput, IPulseStream
{
    private const int Block = AudioFormat.BlockSize;
    private const int CapacityMs = 500;
    private readonly PulseBackend? backend;
    private readonly SampleQueue queue = new(AudioFormat.SampleRate * CapacityMs / 1000, AudioFormat.SampleRate * 5 / 1000, Block);
    private readonly float[] block = new float[Block];
    private IntPtr stream;
    private Thread? thread;
    private volatile bool stop;
    private volatile Exception? failure;
    private int disposed;

    public PulseSpeaker(PulseBackend? backend, string device)
    {
        this.backend = backend;
        Device = device;
        stream = PulseSimple.Open(PulseSimple.Playback, device, "Voice", Block * sizeof(float));
        AppLog.Info("Playback device opened: " + device + " (PulseAudio, mono 48000 Hz)");
    }

    public string Device { get; }
    public bool IsInput => false;

    public int BufferedSamples => queue.Count;

    public void Start()
    {
        if (thread != null) return;
        thread = new Thread(Run) { IsBackground = true, Name = "EasyVocal speaker", Priority = ThreadPriority.Highest };
        thread.Start();
    }

    private void Run()
    {
        try
        {
            while (!stop)
            {
                queue.Read(block);
                int error = 0;
                fixed (float* p = block)
                    if (PulseSimple.pa_simple_write(stream, p, (nuint)(Block * sizeof(float)), &error) < 0)
                    {
                        Fail(new IOException(Device + " stopped: " + PulseSimple.ErrorText(error)));
                        return;
                    }
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    public void Fail(Exception why)
    {
        if (failure != null) return;
        failure = why;
        stop = true; // nothing more goes to a stream the server may have moved elsewhere
        AppLog.Info("Playback device lost: " + why.Message);
    }

    public void Write(ReadOnlySpan<float> mono)
    {
        var why = failure;
        if (why != null) throw new IOException("The audio output stopped.", why);
        if (Volatile.Read(ref disposed) == 1) throw new ObjectDisposedException(nameof(PulseSpeaker));
        queue.Write(mono);
    }

    public void TrimTo(int keepSamples) => queue.TrimTo(keepSamples);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        stop = true;
        if (thread != null && !thread.Join(TimeSpan.FromSeconds(2))) AppLog.Info("The playback thread didn't stop in time: " + Device);
        if (thread == null || !thread.IsAlive) PulseSimple.pa_simple_free(stream);
        stream = IntPtr.Zero;
        backend?.Closed(this);
    }
}
