using System.Diagnostics;
using System.IO;
using System.Threading;
using EasyVocal.Audio;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;

namespace EasyVocal.E2E;

/// <summary>
/// The e2e test's devices: one microphone that plays a known voice in real time on its own thread, headphones and
/// (when "installed") VB-CABLE, both recording what they are given. The test plugs and unplugs them and the
/// controller sees it through <see cref="Changed"/>, raised on another thread like Windows does.
/// </summary>
internal sealed class TestAudioDevices : IAudioDevices
{
    public const string MicId = "e2e-mic";
    public const string HeadphonesId = "e2e-headphones";
    public const string CableId = "e2e-cable-input";
    private const string CableMicId = "e2e-cable-output";

    private readonly object sync = new();
    private readonly float[] voice = E2ESignal.Voice();
    private readonly Dictionary<string, CaptureOutput> latest = new();
    private bool micPresent = true, headphonesPresent = true, cablePresent;
    private TestMic? mic;

    public TestAudioDevices(bool cablePresent) => this.cablePresent = cablePresent;

    /// <summary>The loop the microphone plays (mono 48 kHz, <see cref="E2ESignal.LoopSeconds"/> long).</summary>
    public float[] VoiceLoop => voice;

    /// <summary>The microphone opened last (the one the engine listens to while it runs).</summary>
    public TestMic? Mic
    {
        get { lock (sync) return mic; }
    }

    /// <summary>The device opened last with this id (VB-CABLE = <see cref="CableId"/>, headphones = <see cref="HeadphonesId"/>).</summary>
    public CaptureOutput? Latest(string id)
    {
        lock (sync) return latest.TryGetValue(id, out var o) ? o : null;
    }

    public bool MicPresent
    {
        get { lock (sync) return micPresent; }
    }

    public bool HeadphonesPresent
    {
        get { lock (sync) return headphonesPresent; }
    }

    public bool CablePresent
    {
        get { lock (sync) return cablePresent; }
    }

    public event Action? Changed;

    public IReadOnlyList<DeviceInfo> Inputs()
    {
        lock (sync)
        {
            var list = new List<DeviceInfo>();
            if (micPresent) list.Add(new DeviceInfo(MicId, "E2E Microphone", true));
            if (cablePresent) list.Add(new DeviceInfo(CableMicId, "CABLE Output (VB-Audio Virtual Cable)", false));
            return list;
        }
    }

    public IReadOnlyList<DeviceInfo> Outputs()
    {
        lock (sync)
        {
            var list = new List<DeviceInfo>();
            if (headphonesPresent) list.Add(new DeviceInfo(HeadphonesId, "E2E Headphones", true));
            if (cablePresent) list.Add(new DeviceInfo(CableId, "CABLE Input (VB-Audio Virtual Cable)", false));
            return list;
        }
    }

    public IAudioInput OpenMic(string id)
    {
        lock (sync)
        {
            if (id != MicId || !micPresent) throw new IOException("No such microphone: " + id);
            mic = new TestMic(voice);
            return mic;
        }
    }

    public IAudioOutput OpenSpeaker(string id)
    {
        lock (sync)
        {
            bool there = id == HeadphonesId ? headphonesPresent : id == CableId && cablePresent;
            if (!there) throw new IOException("No such playback device: " + id);
            var o = new CaptureOutput(id, this);
            latest[id] = o;
            return o;
        }
    }

    /// <summary>VB-CABLE installed (true) or removed.</summary>
    public void SetCable(bool present) => Change(() => cablePresent = present);

    /// <summary>
    /// Unplugs the headphones: the one in use starts failing (its writes throw, as WASAPI does) and the device leaves
    /// the list. True plugs them back.
    /// </summary>
    public void SetHeadphones(bool present) => Change(() =>
    {
        headphonesPresent = present;
        if (!present && latest.TryGetValue(HeadphonesId, out var o)) o.Failing = true;
    });

    /// <summary>Unplugs the microphone (its capture ends with an IOException, like WASAPI's) or plugs it back.</summary>
    public void SetMic(bool present)
    {
        TestMic? gone = null;
        Change(() =>
        {
            micPresent = present;
            if (!present) gone = mic;
        });
        gone?.Fail(new IOException("The microphone was unplugged (e2e)."));
    }

    private void Change(Action change)
    {
        lock (sync) change();
        ThreadPool.QueueUserWorkItem(_ => Changed?.Invoke()); // Windows reports device changes on its own threads
    }
}

/// <summary>
/// A microphone playing <see cref="TestAudioDevices.VoiceLoop"/> on a background thread, one 10 ms block at a time and
/// paced by the clock (like a capture thread). <see cref="Clock"/> counts the samples delivered so far.
/// </summary>
internal sealed class TestMic : IAudioInput
{
    private const int Block = AudioFormat.BlockSize;
    private readonly float[] source;
    private readonly float[] buffer = new float[Block];
    private readonly Thread thread;
    private volatile bool stop;
    private volatile Exception? failure;
    private long clock, blockStart;

    public TestMic(float[] source)
    {
        this.source = source;
        thread = new Thread(Run) { IsBackground = true, Name = "E2E microphone" };
    }

    public event Action<float[], int>? Samples;
    public event Action<Exception?>? Stopped;

    /// <summary>Samples delivered since <see cref="Start"/>.</summary>
    public long Clock => Volatile.Read(ref clock);

    /// <summary>Where the block being delivered starts (in <see cref="Clock"/> units): what an output written now receives.</summary>
    public long BlockStart => Volatile.Read(ref blockStart);

    public void Start() => thread.Start();

    /// <summary>Ends the capture with this error on the capture thread (an unplugged device).</summary>
    public void Fail(Exception error) => failure = error;

    private void Run()
    {
        var watch = Stopwatch.StartNew();
        long delivered = 0;
        while (!stop)
        {
            long due = (long)(watch.Elapsed.TotalSeconds * AudioFormat.SampleRate);
            while (!stop && failure == null && delivered + Block <= due)
            {
                for (int i = 0; i < Block; i++) buffer[i] = source[(int)((delivered + i) % source.Length)];
                Volatile.Write(ref blockStart, delivered);
                Samples?.Invoke(buffer, Block);
                delivered += Block;
                Volatile.Write(ref clock, delivered);
            }
            if (failure is { } why && !stop)
            {
                Stopped?.Invoke(why);
                return;
            }
            Thread.Sleep(2);
        }
    }

    public void Dispose()
    {
        stop = true;
        if (thread.IsAlive && Thread.CurrentThread != thread) thread.Join(2000);
    }
}

/// <summary>
/// A playback device that records what it is given in a ring of 30 s, lock-free (one writer: the audio thread).
/// It notes which microphone block its first samples came from, so what it recorded can be lined up with what the
/// microphone played.
/// </summary>
internal sealed class CaptureOutput : IAudioOutput
{
    private const int Capacity = AudioFormat.SampleRate * 30;
    private readonly TestAudioDevices owner;
    private readonly float[] ring = new float[Capacity];
    private long total;
    private long firstMicSample = -1;
    private TestMic? firstMic;
    private volatile bool disposed;

    public CaptureOutput(string deviceId, TestAudioDevices owner)
    {
        DeviceId = deviceId;
        this.owner = owner;
    }

    public string DeviceId { get; }

    /// <summary>Writes throw (an unplugged device).</summary>
    public volatile bool Failing;

    public bool Started { get; private set; }

    public bool Disposed => disposed;

    /// <summary>Samples recorded so far.</summary>
    public long Total => Volatile.Read(ref total);

    public int BufferedSamples => 0; // played at once: never trimmed

    public void Start() => Started = true;

    public void TrimTo(int keepSamples) { }

    // Audio thread.
    public void Write(ReadOnlySpan<float> mono)
    {
        if (Failing) throw new IOException("The playback device was unplugged (e2e).");
        if (disposed) throw new ObjectDisposedException(nameof(CaptureOutput));
        long at = total;
        if (at == 0 && firstMic == null)
        {
            firstMic = owner.Mic;
            firstMicSample = firstMic?.BlockStart ?? 0;
        }
        for (int i = 0; i < mono.Length; i++) ring[(int)((at + i) % Capacity)] = mono[i];
        Volatile.Write(ref total, at + mono.Length);
    }

    /// <summary>
    /// What this device received while <paramref name="mic"/> played its samples [<paramref name="micFrom"/>,
    /// <paramref name="micFrom"/> + length), or null when it wasn't recording then (or that is too long ago).
    /// </summary>
    public float[]? Read(TestMic mic, long micFrom, int length)
    {
        long have = Total;
        if (!ReferenceEquals(firstMic, mic)) return null;
        long from = micFrom - firstMicSample;
        if (from < 0 || from + length > have || have - from > Capacity) return null;
        var o = new float[length];
        for (int i = 0; i < length; i++) o[i] = ring[(int)((from + i) % Capacity)];
        return o;
    }

    public void Dispose() => disposed = true;
}
