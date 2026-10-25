using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Linux;

/// <summary>
/// Linux' sound devices through the PulseAudio protocol (PulseAudio itself, or PipeWire with pipewire-pulse): the list
/// and the defaults from <c>pactl</c>, the streams through libpulse-simple, so every sink and source is reachable by name
/// — the virtual mic EasyVocal creates included (ALSA through PortAudio can't address them one by one).
/// <para>
/// The UI only ever reads the last snapshot; pactl runs on a worker thread of its own. One long-running
/// <c>pactl subscribe</c> tells when to look again (a sink or source came or went, the defaults changed); only while it
/// isn't running does a slow poll stand in. A look that fails keeps the last good snapshot. Each new snapshot fails the
/// open streams whose device left it (the server would move them elsewhere). The defaults are never changed.
/// </para>
/// </summary>
public sealed class PulseBackend : IAudioBackend, IDisposable
{
    /// <summary>The poll while <c>pactl subscribe</c> isn't running.</summary>
    public static readonly TimeSpan FallbackPoll = TimeSpan.FromSeconds(10);
    /// <summary>Several events come together: look once they settle.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    private readonly Pactl pactl;
    private readonly object sync = new();
    private readonly List<IPulseStream> streams = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<bool> firstLook = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread? worker;
    private Snapshot current = Snapshot.Empty;
    private volatile bool subscribed;

    /// <summary>What <c>pactl</c> said last (the monitors of sinks left out of the inputs).</summary>
    public sealed record Snapshot(IReadOnlyList<DeviceInfo> Inputs, IReadOnlyList<DeviceInfo> Outputs, string? DefaultSource, string? DefaultSink)
    {
        public static readonly Snapshot Empty = new(Array.Empty<DeviceInfo>(), Array.Empty<DeviceInfo>(), null, null);

        public string Fingerprint() =>
            string.Join("|", Inputs.Select(d => d.Id + "/" + d.Name)) + "#" + string.Join("|", Outputs.Select(d => d.Id + "/" + d.Name)) +
            "#" + DefaultSource + "#" + DefaultSink;

        public static Snapshot From(PulseListing listing)
        {
            var outputs = new List<DeviceInfo>();
            foreach (var d in listing.Sinks) outputs.Add(new DeviceInfo(d.Name, d.Description, d.Name == listing.Defaults.Sink));
            var inputs = new List<DeviceInfo>();
            foreach (var d in listing.Sources)
                if (!d.IsMonitor) inputs.Add(new DeviceInfo(d.Name, d.Description, d.Name == listing.Defaults.Source));
            return new Snapshot(inputs, outputs, listing.Defaults.Source, listing.Defaults.Sink);
        }
    }

    /// <param name="watch">False in the tests: no worker, no subscription (they call <see cref="Refresh"/>).</param>
    public PulseBackend(Pactl? pactl = null, bool watch = true)
    {
        this.pactl = pactl ?? Pactl.Default;
        if (!watch)
        {
            firstLook.TrySetResult(true);
            return;
        }
        worker = new Thread(Work) { IsBackground = true, Name = "EasyVocal sound devices" };
        worker.Start();
        _ = SubscribeAsync(stop.Token);
    }

    /// <summary>Completes after the first look: true when the server answered (false: no PulseAudio / PipeWire server).</summary>
    public Task<bool> FirstLook => firstLook.Task;

    /// <summary>True while <c>pactl subscribe</c> runs (the slow poll is off then).</summary>
    public bool Subscribed => subscribed;

    public event Action? Changed;

    public IReadOnlyList<DeviceInfo> Inputs() => Current.Inputs;

    public IReadOnlyList<DeviceInfo> Outputs() => Current.Outputs;

    private Snapshot Current
    {
        get { lock (sync) return current; }
    }

    public DefaultIds Defaults(bool playback)
    {
        var s = Current;
        var id = playback ? s.DefaultSink : s.DefaultSource;
        return new DefaultIds(id, id);
    }

    /// <summary>EasyVocal never changes the system's default devices on Linux.</summary>
    public bool SetDefault(string id) => false;

    /// <summary>
    /// Opens a source that is in the snapshot. EasyVocal's own microphone (or its output's monitor) is never one: it would
    /// send the voice back into itself.
    /// </summary>
    public IAudioInput OpenMic(string id)
    {
        var device = Current.Inputs.FirstOrDefault(d => d.Id == id)
            ?? throw new IOException("The microphone is not connected: " + id);
        if (IsOwnDevice(device)) throw new IOException("EasyVocal's own microphone can't be its microphone (it would loop the voice): " + id);
        var mic = new PulseMic(this, id);
        lock (sync) streams.Add(mic);
        return mic;
    }

    /// <summary>Opens a sink that is in the snapshot.</summary>
    public IAudioOutput OpenSpeaker(string id)
    {
        if (!Current.Outputs.Any(d => d.Id == id)) throw new IOException("The playback device is not connected: " + id);
        var speaker = new PulseSpeaker(this, id);
        lock (sync) streams.Add(speaker);
        return speaker;
    }

    /// <summary>A device of EasyVocal's own (its virtual mic, its output, their monitors).</summary>
    public static bool IsOwnDevice(DeviceInfo d) =>
        CableNames.EasyVocalLinux.IsCable(d.Name) || CableNames.EasyVocalLinux.IsCable(d.Id);

    /// <summary>For the tests: watch a stream the way an opened one is watched.</summary>
    internal void Track(IPulseStream stream)
    {
        lock (sync) streams.Add(stream);
    }

    internal void Closed(IPulseStream stream)
    {
        lock (sync) streams.Remove(stream);
    }

    /// <summary>Asks the worker to look again soon (any thread; never waits).</summary>
    public void Invalidate() => wake.Set();

    /// <summary>
    /// Looks now, on the calling thread (never the UI's: the worker, the virtual mic's creation, the tests). A failed look
    /// keeps the last good snapshot. Open streams whose device left the list fail; a changed list raises <see cref="Changed"/>.
    /// </summary>
    public Snapshot Refresh()
    {
        try
        {
            var listing = pactl.List();
            firstLook.TrySetResult(listing.Ok);
            if (!listing.Ok) return Current; // the server didn't answer this time: keep what it said last
            var fresh = Snapshot.From(listing);
            Snapshot before;
            IPulseStream[] open;
            lock (sync)
            {
                before = current;
                current = fresh;
                open = streams.ToArray();
            }
            foreach (var s in open)
            {
                var list = s.IsInput ? fresh.Inputs : fresh.Outputs;
                if (!list.Any(d => d.Id == s.Device)) s.Fail(new IOException(s.Device + " is gone."));
            }
            if (fresh.Fingerprint() != before.Fingerprint()) Changed?.Invoke();
            return fresh;
        }
        catch (Exception ex)
        {
            AppLog.Error("Reading the sound devices failed", ex);
            return Current;
        }
    }

    /// <summary>The worker: a look at start, then one after every wake (events settle first), or the slow poll without a subscription.</summary>
    private void Work()
    {
        Refresh();
        while (!stop.IsCancellationRequested)
        {
            bool woken = wake.WaitOne(subscribed ? Timeout.InfiniteTimeSpan : FallbackPoll);
            if (stop.IsCancellationRequested) return;
            if (woken)
            {
                Thread.Sleep(Settle);
                wake.Reset(); // the events of the same moment count once
            }
            Refresh();
        }
    }

    /// <summary><c>pactl subscribe</c>, started again (a little later each time) whenever it ends.</summary>
    private async Task SubscribeAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var tool = pactl.Locate();
            if (tool != null)
            {
                subscribed = true;
                var started = DateTime.UtcNow;
                int code = await pactl.CommandLine.StreamAsync(tool, new[] { "subscribe" }, line =>
                {
                    if (Pactl.MattersForDevices(line)) wake.Set();
                }, ct).ConfigureAwait(false);
                subscribed = false;
                if (ct.IsCancellationRequested) return;
                AppLog.Info("pactl subscribe ended (" + code + "): polling the sound devices until it runs again");
                wake.Set(); // look now, and from now on every few seconds
                if (DateTime.UtcNow - started > TimeSpan.FromMinutes(1)) backoff = TimeSpan.FromSeconds(1);
            }
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        wake.Set();
        worker?.Join(TimeSpan.FromSeconds(2));
    }
}
