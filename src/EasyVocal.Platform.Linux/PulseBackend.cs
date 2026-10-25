using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Linux;

/// <summary>
/// Linux' sound devices through the PulseAudio protocol (PulseAudio itself, or PipeWire with pipewire-pulse): the list
/// and the defaults from <c>pactl</c>, the streams through libpulse-simple, so every sink and source is reachable by name
/// — the virtual mic EasyVocal creates included (ALSA through PortAudio can't address them one by one). The list is read
/// again at most once a second and every 2 s by a poll that raises <see cref="Changed"/>. The defaults are never changed.
/// </summary>
public sealed class PulseBackend : IAudioBackend, IDisposable
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(1);

    private readonly Pactl pactl;
    private readonly object sync = new();
    private readonly Timer? poll;
    private Snapshot current = Snapshot.Empty;
    private DateTime readAt = DateTime.MinValue;
    private int openStreams, polling;
    private bool disposed;

    /// <summary>What <c>pactl</c> said last.</summary>
    public sealed record Snapshot(IReadOnlyList<DeviceInfo> Inputs, IReadOnlyList<DeviceInfo> Outputs, string? DefaultSource, string? DefaultSink)
    {
        public static readonly Snapshot Empty = new(Array.Empty<DeviceInfo>(), Array.Empty<DeviceInfo>(), null, null);

        public string Fingerprint() =>
            string.Join("|", Inputs.Select(d => d.Id + "/" + d.Name)) + "#" + string.Join("|", Outputs.Select(d => d.Id + "/" + d.Name)) +
            "#" + DefaultSource + "#" + DefaultSink;
    }

    /// <param name="poll">False in the tests: no timer (they call <see cref="Refresh"/>).</param>
    public PulseBackend(Pactl? pactl = null, bool poll = true)
    {
        this.pactl = pactl ?? Pactl.Default;
        if (poll) this.poll = new Timer(_ => Poll(), null, PollEvery, PollEvery);
    }

    /// <summary>A PulseAudio-protocol server answers (<c>pactl info</c> works).</summary>
    public static bool ServerAnswers(Pactl? pactl = null) => (pactl ?? Pactl.Default).Run("info").Ok;

    public event Action? Changed;

    public IReadOnlyList<DeviceInfo> Inputs() => Fresh().Inputs;

    public IReadOnlyList<DeviceInfo> Outputs() => Fresh().Outputs;

    public DefaultIds Defaults(bool playback)
    {
        var s = Fresh();
        var id = playback ? s.DefaultSink : s.DefaultSource;
        return new DefaultIds(id, id);
    }

    /// <summary>EasyVocal never changes the system's default devices on Linux.</summary>
    public bool SetDefault(string id) => false;

    public IAudioInput OpenMic(string id)
    {
        var mic = new PulseMic(this, id);
        Interlocked.Increment(ref openStreams);
        return mic;
    }

    public IAudioOutput OpenSpeaker(string id)
    {
        var speaker = new PulseSpeaker(this, id);
        Interlocked.Increment(ref openStreams);
        return speaker;
    }

    internal void Closed() => Interlocked.Decrement(ref openStreams);

    /// <summary>The list may be out of date (EasyVocal just made or removed its virtual mic): the next look reads it again.</summary>
    public void Invalidate()
    {
        lock (sync) readAt = DateTime.MinValue;
    }

    private Snapshot Fresh()
    {
        lock (sync)
        {
            if (DateTime.UtcNow - readAt < FreshFor) return current;
        }
        return Refresh();
    }

    /// <summary>Reads the devices and defaults again. Never throws (an empty list when pactl fails).</summary>
    public Snapshot Refresh()
    {
        var read = Read(pactl);
        lock (sync)
        {
            current = read;
            readAt = DateTime.UtcNow;
            return read;
        }
    }

    /// <summary>One look through pactl: sinks, sources (monitors left out) and the defaults.</summary>
    internal static Snapshot Read(Pactl pactl)
    {
        try
        {
            var info = pactl.Run("info");
            if (!info.Ok) return Snapshot.Empty;
            var defaultSink = Pactl.ParseDefault(info.Output, playback: true);
            var defaultSource = Pactl.ParseDefault(info.Output, playback: false);
            var sinks = pactl.Run("list", "sinks");
            var sources = pactl.Run("list", "sources");
            var outputs = new List<DeviceInfo>();
            foreach (var d in Pactl.ParseList(sinks.Output)) outputs.Add(new DeviceInfo(d.Name, d.Description, d.Name == defaultSink));
            var inputs = new List<DeviceInfo>();
            foreach (var d in Pactl.ParseList(sources.Output))
                if (!d.IsMonitor) inputs.Add(new DeviceInfo(d.Name, d.Description, d.Name == defaultSource));
            return new Snapshot(inputs, outputs, defaultSource, defaultSink);
        }
        catch (Exception ex)
        {
            AppLog.Error("Listing the sound devices failed", ex);
            return Snapshot.Empty;
        }
    }

    private void Poll()
    {
        if (disposed || Interlocked.Exchange(ref polling, 1) == 1) return;
        try
        {
            string before;
            lock (sync) before = current.Fingerprint();
            var now = Refresh();
            if (now.Fingerprint() != before) Changed?.Invoke();
        }
        catch (Exception ex) { AppLog.Error("Watching the sound devices failed", ex); }
        finally { Volatile.Write(ref polling, 0); }
    }

    public void Dispose()
    {
        disposed = true;
        poll?.Dispose();
    }
}
