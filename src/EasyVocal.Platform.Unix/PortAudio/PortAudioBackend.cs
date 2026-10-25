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
    /// <summary>The active devices. Never throws (an empty list).</summary>
    IReadOnlyList<SystemDevice> Devices();

    /// <summary>The system's default microphone / playback device (an <see cref="SystemDevice.Id"/>), or null.</summary>
    string? DefaultId(bool playback);
}

/// <summary>
/// Sound devices through PortAudio (macOS: CoreAudio underneath; Linux without PulseAudio/PipeWire: ALSA). Mono float at
/// 48 kHz, 10 ms blocks. The list comes from the <see cref="IDeviceSource"/> when there is one, else from PortAudio
/// itself. PortAudio learns about new devices only when it is started again, which happens whenever a stream is opened
/// while none is open (the engine restarts all its devices when the routes change). Every 2 s a poll looks for list
/// changes (<see cref="Changed"/>) and for streams that stopped by themselves (unplugged): those report it.
/// The system's defaults can't be changed here (<see cref="SetDefault"/> is false).
/// </summary>
public sealed class PortAudioBackend : IAudioBackend, IDisposable
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    private readonly object sync = new();
    private readonly IDeviceSource? source;
    private readonly Func<bool>? micRefused;
    private readonly List<PortAudioStream> streams = new();
    private readonly Timer poll;
    private List<PaDevice> paDevices = new();
    private bool initialized, stale = true, unavailable, disposed;
    private string fingerprint = "";
    private string? problem;
    private int polling;

    /// <summary>A device as PortAudio lists it right now (its index changes whenever PortAudio is started again).</summary>
    internal sealed record PaDevice(int Index, string Id, string Name, int MaxInputs, int MaxOutputs, bool DefaultIn, bool DefaultOut,
        double LowInputLatency, double LowOutputLatency, double DefaultRate);

    /// <param name="source">The system's own device list (macOS: CoreAudio); null = PortAudio's.</param>
    /// <param name="micRefused">True while the system refuses EasyVocal the microphone (macOS privacy); null = never.</param>
    public PortAudioBackend(IDeviceSource? source = null, Func<bool>? micRefused = null)
    {
        this.source = source;
        this.micRefused = micRefused;
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
        lock (sync)
        {
            var device = Resolve(id, input: false);
            var speaker = new PortAudioSpeaker(this, device);
            streams.Add(speaker);
            return speaker;
        }
    }

    /// <summary>A stream closed (from its Dispose).</summary>
    internal void Closed(PortAudioStream stream)
    {
        lock (sync) streams.Remove(stream);
    }

    /// <summary>The lock every PortAudio call takes (it is not thread-safe while streams open and close).</summary>
    internal object Sync => sync;

    /// <summary>The PortAudio device for an id: found in a fresh list when no stream is open, else in the current one.</summary>
    private PaDevice Resolve(string id, bool input)
    {
        RefreshIfIdle();
        if (unavailable) throw new IOException("Sound devices aren't available: " + problem);
        var match = Match(id, input);
        if (match == null && streams.Count == 0 && !stale)
        {
            stale = true; // plugged in a moment ago: PortAudio may not list it yet
            RefreshIfIdle();
            match = Match(id, input);
        }
        return match ?? throw new IOException("The sound device is not connected: " + id);
    }

    private PaDevice? Match(string id, bool input)
    {
        bool Can(PaDevice d) => input ? d.MaxInputs > 0 : d.MaxOutputs > 0;
        if (source == null)
        {
            foreach (var d in paDevices)
                if (d.Id == id && Can(d)) return d;
            return null;
        }
        // The system's id → its name → PortAudio's device of that name.
        string? name = null;
        foreach (var d in source.Devices())
            if (d.Id == id) name = d.Name;
        if (name == null) return null;
        foreach (var d in paDevices)
            if (Can(d) && d.Name == name) return d;
        foreach (var d in paDevices)
            if (Can(d) && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
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

    /// <summary>Every 2 s (timer thread): a changed list raises <see cref="Changed"/>; a stream that stopped by itself reports it.</summary>
    private void Poll()
    {
        if (Interlocked.Exchange(ref polling, 1) == 1) return; // the last round is still running
        try
        {
            string now;
            PortAudioStream[] open;
            lock (sync)
            {
                if (disposed) return;
                if (source == null && streams.Count == 0) stale = true; // nothing open: PortAudio may be started again
                if (source == null) RefreshIfIdle();
                open = streams.ToArray();
                now = source == null ? Fingerprint(paDevices) : "";
            }
            if (source != null) now = Fingerprint(source.Devices(), source.DefaultId(false), source.DefaultId(true));
            bool refused = micRefused?.Invoke() == true;
            foreach (var s in open) s.Check(refused);
            if (now == fingerprint) return;
            bool first = fingerprint.Length == 0;
            fingerprint = now;
            lock (sync) stale = true; // the next open with nothing else open starts PortAudio again
            if (!first) Changed?.Invoke();
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
        lock (sync)
        {
            if (initialized) PortAudioNative.Pa_Terminate();
            initialized = false;
        }
    }
}
