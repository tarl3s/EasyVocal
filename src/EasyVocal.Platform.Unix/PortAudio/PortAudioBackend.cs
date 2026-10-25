using System.Runtime.InteropServices;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Unix;

/// <summary>One sound device as the system lists it (macOS: CoreAudio). <see cref="Id"/> is stable across runs.</summary>
public sealed record SystemDevice(string Id, string Name, bool Input, bool Output);

/// <summary>
/// Where the device list comes from when the system can tell more than PortAudio (macOS: CoreAudio sees a device the
/// moment it is plugged in; PortAudio only after it is started again, which it can't be while a stream is open).
/// </summary>
public interface IDeviceSource
{
    /// <summary>The active devices, in the system's order. Never throws (an empty list).</summary>
    IReadOnlyList<SystemDevice> Devices();

    /// <summary>The system's default microphone / playback device (an <see cref="SystemDevice.Id"/>), or null.</summary>
    string? DefaultId(bool playback);
}

/// <summary>
/// Sound devices through PortAudio (macOS: CoreAudio underneath; Linux without PulseAudio/PipeWire: ALSA). Mono float at
/// 48 kHz, 10 ms blocks.
/// <para>
/// The list comes from the <see cref="IDeviceSource"/> when there is one, else from PortAudio itself. PortAudio learns
/// about new devices only when it is started again, which can happen only while no stream is open: it is started again
/// when a stream is opened with none open after the devices changed. What says they changed: the source's list
/// (macOS), else a cheap look at the system (<c>systemFingerprint</c>: Linux' /proc/asound/cards and /dev/snd) — never a
/// restart on a timer. Without a source, a change while streams are open fails them, so the engine opens everything
/// again on the new list. Every 2 s a poll also fails streams that stopped by themselves (unplugged).
/// The system's defaults can't be changed here (<see cref="SetDefault"/> is false).
/// </para>
/// </summary>
public sealed class PortAudioBackend : IAudioBackend, IDisposable
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
    /// <summary>A device asked for that PortAudio doesn't list: PortAudio is started again at most this often for it.</summary>
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);

    private readonly object sync = new();
    private readonly IDeviceSource? source;
    private readonly Func<bool>? micRefused;
    private readonly Func<string>? systemFingerprint;
    private readonly List<PortAudioStream> streams = new(); // open ones, and those still closing
    private readonly HashSet<PortAudioStream> closing = new(); // its own lock: a stream's Dispose never waits for sync
    private readonly Timer poll;
    private List<PaDevice> paDevices = new();
    private bool initialized, stale = true, unavailable, disposed;
    private DateTime lastRetry = DateTime.MinValue;
    private string fingerprint = "", systemLook = "";
    private string? problem;
    private int polling;

    /// <summary>A device as PortAudio lists it right now (its index changes whenever PortAudio is started again).</summary>
    internal sealed record PaDevice(int Index, string Id, string Name, int MaxInputs, int MaxOutputs, bool DefaultIn, bool DefaultOut,
        double LowInputLatency, double LowOutputLatency, double DefaultRate);

    /// <param name="source">The system's own device list (macOS: CoreAudio); null = PortAudio's.</param>
    /// <param name="micRefused">True while the system refuses EasyVocal the microphone (macOS privacy); null = never.</param>
    /// <param name="systemFingerprint">Without a source: something that changes when the devices do (Linux: ALSA's cards); null = none.</param>
    public PortAudioBackend(IDeviceSource? source = null, Func<bool>? micRefused = null, Func<string>? systemFingerprint = null)
    {
        this.source = source;
        this.micRefused = micRefused;
        this.systemFingerprint = systemFingerprint;
        systemLook = systemFingerprint?.Invoke() ?? "";
        poll = new Timer(_ => Poll(), null, PollEvery, PollEvery);
    }

    /// <summary>Why there are no devices at all (the library couldn't be loaded or started), for the log; null when fine.</summary>
    public string? Problem
    {
        get { lock (sync) return problem; }
    }

    public event Action? Changed;

    public IReadOnlyList<DeviceInfo> Inputs() => List(playback: false);

    public IReadOnlyList<DeviceInfo> Outputs() => List(playback: true);

    private IReadOnlyList<DeviceInfo> List(bool playback)
    {
        if (source != null)
        {
            var defaultId = source.DefaultId(playback);
            var list = new List<DeviceInfo>();
            foreach (var d in source.Devices())
                if (playback ? d.Output : d.Input) list.Add(new DeviceInfo(d.Id, d.Name, d.Id == defaultId));
            return list;
        }
        lock (sync)
        {
            RefreshIfIdle();
            var list = new List<DeviceInfo>();
            foreach (var d in paDevices)
                if (playback ? d.MaxOutputs > 0 : d.MaxInputs > 0) list.Add(new DeviceInfo(d.Id, d.Name, playback ? d.DefaultOut : d.DefaultIn));
            return list;
        }
    }

    public DefaultIds Defaults(bool playback)
    {
        string? id = null;
        foreach (var d in List(playback))
            if (d.IsDefault) id = d.Id;
        return new DefaultIds(id, id); // one default for every use on macOS and Linux
    }

    /// <summary>EasyVocal never changes the system's default devices here.</summary>
    public bool SetDefault(string id) => false;

    public IAudioInput OpenMic(string id)
    {
        if (micRefused?.Invoke() == true) throw new MicPermissionDeniedException();
        WaitForClosing(OpenWait);
        lock (sync)
        {
            var device = Resolve(id, input: true);
            var mic = new PortAudioMic(this, device);
            streams.Add(mic);
            return mic;
        }
    }

    public IAudioOutput OpenSpeaker(string id)
    {
        WaitForClosing(OpenWait);
        lock (sync)
        {
            var device = Resolve(id, input: false);
            var speaker = new PortAudioSpeaker(this, device);
            streams.Add(speaker);
            return speaker;
        }
    }

    /// <summary>A stream is being closed on a thread of its own (see <see cref="PortAudioStream"/>). Never takes <see cref="sync"/>.</summary>
    internal void Closing(PortAudioStream stream)
    {
        lock (closing) closing.Add(stream);
    }

    /// <summary>A stream closed (<paramref name="stillOpen"/>: left open on purpose, so PortAudio is never restarted under it).</summary>
    internal void Closed(PortAudioStream stream, bool stillOpen)
    {
        if (!stillOpen)
            lock (sync) streams.Remove(stream);
        lock (closing) closing.Remove(stream);
    }

    /// <summary>
    /// Waits (without the lock) until no stream is closing, at most <paramref name="limit"/>; true when none is. A stream
    /// opens only once the last one closed: on macOS opening one while another's audio thread stops it could freeze.
    /// </summary>
    internal bool WaitForClosing(TimeSpan limit)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (closing)
                if (closing.Count == 0) return true;
            if (watch.Elapsed >= limit) return false;
            Thread.Sleep(2);
        }
    }

    /// <summary>How long an open waits for streams still closing (a stream closes within a few blocks).</summary>
    private static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(1);

    /// <summary>The lock every PortAudio call takes (it is not thread-safe while streams open and close).</summary>
    internal object Sync => sync;

    /// <summary>The PortAudio device for an id: found in a fresh list when no stream is open, else in the current one.</summary>
    private PaDevice Resolve(string id, bool input)
    {
        RefreshIfIdle();
        if (unavailable) throw new IOException("Sound devices aren't available: " + problem);
        var match = Match(id, input);
        if (match == null && streams.Count == 0 && !stale && DateTime.UtcNow - lastRetry > RetryEvery)
        {
            lastRetry = DateTime.UtcNow;
            stale = true; // plugged in a moment ago: PortAudio may not list it yet (tried again at most every 2 s)
            RefreshIfIdle();
            match = Match(id, input);
        }
        return match ?? throw new IOException("The sound device is not connected: " + id);
    }

    private PaDevice? Match(string id, bool input) => source == null
        ? paDevices.FirstOrDefault(d => d.Id == id && Can(d, input))
        : MatchSystem(source.Devices(), paDevices, id, input);

    private static bool Can(PaDevice d, bool input) => input ? d.MaxInputs > 0 : d.MaxOutputs > 0;

    /// <summary>
    /// The system's id → PortAudio's device: by name, and when several devices share a name (two identical USB headsets),
    /// the n-th of that name on each side, in the system's order (the order PortAudio reads them in).
    /// </summary>
    internal static PaDevice? MatchSystem(IReadOnlyList<SystemDevice> system, IReadOnlyList<PaDevice> pa, string id, bool input)
    {
        var wanted = system.FirstOrDefault(d => d.Id == id);
        if (wanted == null) return null;
        int nth = 0;
        foreach (var d in system)
        {
            if (d.Id == id) break;
            if (d.Name == wanted.Name && (input ? d.Input : d.Output)) nth++;
        }
        var named = pa.Where(d => Can(d, input) && d.Name == wanted.Name).ToList();
        if (named.Count == 0) named = pa.Where(d => Can(d, input) && string.Equals(d.Name, wanted.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        return nth < named.Count ? named[nth] : null;
    }

    /// <summary>Under <see cref="sync"/>: starts PortAudio again for a fresh list, only while no stream is open.</summary>
    private void RefreshIfIdle()
    {
        if (disposed || (initialized && (!stale || streams.Count > 0))) return;
        try
        {
            if (initialized) PortAudioNative.Pa_Terminate();
            initialized = false;
            int error = PortAudioNative.Pa_Initialize();
            if (error != PortAudioNative.NoError) throw new PortAudioException("Starting PortAudio", error);
            initialized = true;
            Starts++;
            paDevices = ReadDevices();
            stale = false;
            unavailable = false;
            problem = null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PortAudioException)
        {
            if (!unavailable) AppLog.Error("PortAudio can't be used: no sound devices", ex);
            unavailable = true;
            stale = false;
            problem = ex.Message;
            paDevices = new List<PaDevice>();
        }
    }

    private static unsafe List<PaDevice> ReadDevices()
    {
        var list = new List<PaDevice>();
        int hostApi = PortAudioNative.Pa_GetDefaultHostApi();
        var host = hostApi >= 0 ? PortAudioNative.Pa_GetHostApiInfo(hostApi) : null;
        int count = PortAudioNative.Pa_GetDeviceCount();
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < count; i++)
        {
            var info = PortAudioNative.Pa_GetDeviceInfo(i);
            if (info == null || info->HostApi != hostApi) continue; // one host API: every device once
            var name = Marshal.PtrToStringUTF8(info->Name) ?? ("Device " + i);
            var hostName = host != null ? Marshal.PtrToStringUTF8(host->Name) ?? "" : "";
            var id = "pa:" + hostName + ":" + name;
            int n = seen.TryGetValue(id, out var k) ? k + 1 : 1;
            seen[id] = n;
            if (n > 1) id += "#" + n; // two devices with one name: told apart by their order
            list.Add(new PaDevice(i, id, name, info->MaxInputChannels, info->MaxOutputChannels,
                host != null && host->DefaultInputDevice == i, host != null && host->DefaultOutputDevice == i,
                info->DefaultLowInputLatency, info->DefaultLowOutputLatency, info->DefaultSampleRate));
        }
        return list;
    }

    /// <summary>
    /// Every 2 s (timer thread): a changed list raises <see cref="Changed"/>; a stream that stopped by itself reports it.
    /// Without a source, PortAudio is started again only when the system's devices changed.
    /// </summary>
    private void Poll()
    {
        if (Interlocked.Exchange(ref polling, 1) == 1) return; // the last round is still running
        try
        {
            PortAudioStream[] open;
            string now;
            bool systemChanged = false;
            if (source == null && systemFingerprint != null)
            {
                var look = systemFingerprint();
                systemChanged = look != systemLook;
                systemLook = look;
            }
            lock (sync)
            {
                if (disposed) return;
                if (systemChanged) stale = true;
                if (source == null) RefreshIfIdle(); // only does anything when stale and nothing is open
                open = streams.ToArray();
                now = source == null ? Fingerprint(paDevices) : "";
            }
            if (source != null) now = Fingerprint(source.Devices(), source.DefaultId(false), source.DefaultId(true));
            bool refused = micRefused?.Invoke() == true;
            foreach (var s in open) s.Check(refused);
            if (systemChanged && source == null && open.Length > 0)
            {
                // The devices changed under open streams: PortAudio can't see it until they close. Failing them makes
                // the engine open everything again on a fresh list (a short gap, like an unplugged device).
                AppLog.Info("The sound devices changed: opening the audio again to see them");
                foreach (var s in open) s.SimulateFailure(new IOException("The sound devices changed."));
            }
            if (now == fingerprint && !systemChanged) return;
            bool first = fingerprint.Length == 0;
            fingerprint = now;
            if (source != null) lock (sync) stale = true; // the next open with nothing else open starts PortAudio again
            if (!first || systemChanged) Changed?.Invoke();
        }
        catch (Exception ex) { AppLog.Error("Watching the sound devices failed", ex); }
        finally { Volatile.Write(ref polling, 0); }
    }

    internal static string Fingerprint(IEnumerable<PaDevice> devices)
    {
        var parts = new List<string>();
        foreach (var d in devices) parts.Add(d.Id + "/" + d.MaxInputs + "/" + d.MaxOutputs + (d.DefaultIn ? "/in" : "") + (d.DefaultOut ? "/out" : ""));
        return string.Join("|", parts);
    }

    internal static string Fingerprint(IEnumerable<SystemDevice> devices, string? defaultIn, string? defaultOut)
    {
        var parts = new List<string> { "in=" + defaultIn, "out=" + defaultOut };
        foreach (var d in devices) parts.Add(d.Id + "/" + d.Name + "/" + d.Input + "/" + d.Output);
        return string.Join("|", parts);
    }

    /// <summary>For the tests: one round of the poll now.</summary>
    internal void PollNow() => Poll();

    /// <summary>How many times PortAudio has been started (for the tests: never on a timer).</summary>
    internal int Starts { get; private set; }

    public void Dispose()
    {
        PortAudioStream[] open;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            open = streams.ToArray();
        }
        poll.Dispose();
        foreach (var s in open) s.Dispose();
        bool allClosed = WaitForClosing(TimeSpan.FromSeconds(2));
        lock (sync)
        {
            // Pa_Terminate aborts what is still open: never under a stream whose audio thread may be stopping it.
            if (initialized && allClosed && streams.Count == 0) PortAudioNative.Pa_Terminate();
            else if (initialized) AppLog.Info("PortAudio is left running: a sound stream is still closing");
            initialized = false;
        }
    }
}
