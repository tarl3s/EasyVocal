using System.IO;
using System.Runtime.Versioning;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EasyVocal.Core.Storage;

namespace EasyVocal;

/// <summary>
/// One EasyVocal per person: the first launch owns the lock; a later launch tells it to show its window and leaves.
/// <para>Windows: a named mutex and a named event (as 0.2.0).</para>
/// <para>
/// Elsewhere: a lock file in the data folder held open with no sharing (.NET takes an advisory <c>flock</c> on it), and a
/// local pipe (a Unix socket under the hood) the second launch writes to. The lock is not trusted alone: on some network
/// file systems (NFS) <c>flock</c> does nothing, and <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1</c> turns it off, so a
/// launch that got the lock first asks the pipe whether an EasyVocal answers. And a lock that can't be taken is not
/// proof either (a stale or unreadable file): when no EasyVocal answers the pipe, this one runs without the lock.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The names one EasyVocal claims (the tests pass their own, so they never meet the person's EasyVocal).</summary>
    /// <param name="Portable">Use the lock file and the pipe even on Windows (tests of the macOS/Linux path).</param>
    public sealed record Names(string MutexName, string ShowEventName, string LockPath, string PipeName, bool Portable = false)
    {
        /// <summary>This person's EasyVocal.</summary>
        public static Names Default => new("EasyVocal.SingleInstance", "EasyVocal.ShowWindow",
            Path.Combine(SettingsStore.DefaultFolder, "instance.lock"), ShortPipeName(Environment.UserName));

        /// <summary>
        /// The file a listening pipe shows up as: its Unix socket on macOS and Linux (see <see cref="SocketPath"/>); null
        /// on Windows, where a pipe has none. The tests name a stand-in to play the socket on any OS.
        /// </summary>
        public string? PipeFile { get; init; } = OperatingSystem.IsWindows() ? null : SocketPath(PipeName);
    }

    /// <summary>After this many pipe failures in a row the listener gives up (a second launch then just starts).</summary>
    private const int MaxPipeFailures = 5;
    /// <summary>The lock is held: an EasyVocal is (very likely) there, maybe still starting its listener — wait for it.</summary>
    private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(2); // a busy computer may take that long to start it
    /// <summary>Only checking (we got the lock): a listener that exists answers at once.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(100);
    /// <summary>A client that connects and never writes can't hold the listener longer than this.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    /// <summary>The byte a second launch writes: "show your window". A connection without it (<see cref="IsRunning"/>) only asks.</summary>
    private const byte ShowByte = 1;

    private readonly Names names;
    private readonly Mutex? mutex;
    private readonly EventWaitHandle? showSignal;
    private readonly RegisteredWaitHandle? showWait;
    private readonly FileStream? lockFile;
    private readonly CancellationTokenSource stop = new();
    private readonly object sync = new();
    private readonly UnixListener? unix;
    private readonly HashSet<Stream> connections = new(); // being read; closed at once on dispose
    private Action? show;
    private bool pendingShow, disposed;

    private SingleInstance(Names names, Mutex mutex, EventWaitHandle showSignal)
    {
        this.names = names;
        this.mutex = mutex;
        this.showSignal = showSignal;
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => RaiseShow(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <param name="listen">False: a pipe file of someone else is there (it must not be taken over), so no listener.</param>
    private SingleInstance(Names names, FileStream? lockFile, bool listen = true)
    {
        this.names = names;
        this.lockFile = lockFile;
        if (!listen) return;
        // macOS/Linux: one Unix socket of our own at the path .NET's pipe clients connect to (a second launch's Knock).
        // .NET's own pipe server shares one socket between its instances and, under a quick connect-write-close, lost a
        // wake now and then on macOS; a plain socket accepts every connection the same way.
        if (!OperatingSystem.IsWindows()) unix = new UnixListener(SocketPath(names.PipeName));
        Listening = ListenAsync(stop.Token);
    }

    /// <summary>The pipe listener (portable path); done when it stopped; null when this one doesn't listen.</summary>
    internal Task? Listening { get; }

    /// <summary>True when this one runs without the lock (it couldn't be taken and nobody answered).</summary>
    public bool WithoutLock => !UsesMutex(names) && lockFile == null;

    /// <summary>
    /// The first instance's handler for "show your window" (raised on a background thread). A wake that came before the
    /// handler was set is handed over now.
    /// </summary>
    public void OnShowRequested(Action handler)
    {
        bool replay;
        lock (sync)
        {
            show = handler;
            replay = pendingShow;
            pendingShow = false;
        }
        if (replay) Invoke(handler);
    }

    private void RaiseShow()
    {
        Action? handler;
        lock (sync)
        {
            handler = show;
            if (handler == null) pendingShow = true;
        }
        if (handler != null) Invoke(handler);
    }

    private static void Invoke(Action handler)
    {
        try { handler(); }
        catch (Exception ex) { AppLog.Error("Showing the window for a second launch failed", ex); }
    }

    [SupportedOSPlatformGuard("windows")]
    private static bool UsesMutex(Names names) => OperatingSystem.IsWindows() && !names.Portable;

    /// <summary>The lock when this is the first EasyVocal, else null (another one runs: it was asked to show its window).</summary>
    public static SingleInstance? TryAcquire(Names? names = null)
    {
        names ??= Names.Default;
        if (UsesMutex(names))
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, names.ShowEventName);
            var m = new Mutex(true, names.MutexName, out bool first);
            if (first) return new SingleInstance(names, m, signal);
            m.Dispose();
            signal.Dispose();
            WakeFirst(names);
            return null;
        }
        switch (TakeLock(names, out var file, out var problem))
        {
            case LockState.Held:
                // Held by the running EasyVocal, or by something else. Only an answer on the pipe says another one runs
                // (waiting a little, for its pipe to appear too: it may be starting its listener right now).
                if (Knock(names, show: true, WakeTimeout, waitForPipe: true)) return null;
                return RunWithoutLock(names, "The single-instance lock couldn't be taken and no EasyVocal answered: running without it (" + problem + ")");
            case LockState.Unusable:
                if (Knock(names, show: true, ProbeTimeout)) return null;
                return RunWithoutLock(names, "The single-instance lock can't be used here and no EasyVocal answered: running without it (" + problem + ")");
            default:
                // Locking may do nothing here (NFS, DOTNET_SYSTEM_IO_DISABLEFILELOCKING): an EasyVocal that answers wins.
                // Usually there is none, so this is only a quick look (no wait at all when nobody ever listened).
                if (Knock(names, show: true, ProbeTimeout))
                {
                    file!.Dispose();
                    return null;
                }
                return new SingleInstance(names, file);
        }
    }

    /// <summary>
    /// Runs without the lock. A pipe file that is there (a socket that didn't answer in time) is never bound again: on
    /// macOS/Linux listening would replace it, and the EasyVocal behind it could never be woken; this one then doesn't listen.
    /// </summary>
    private static SingleInstance RunWithoutLock(Names names, string why)
    {
        bool taken = names.PipeFile is { } file && File.Exists(file);
        AppLog.Info(why + (taken ? "; a pipe that didn't answer is there (an EasyVocal still starting?), so this one doesn't listen for later launches" : ""));
        return new SingleInstance(names, null, listen: !taken);
    }

    private enum LockState { Taken, Held, Unusable }

    /// <summary>Tries the lock file: taken (the stream holds it), held by another process, or unusable (no rights...).</summary>
    private static LockState TakeLock(Names names, out FileStream? file, out string? problem)
    {
        file = null;
        problem = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(names.LockPath)!);
            file = new FileStream(names.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return LockState.Taken;
        }
        catch (IOException ex)
        {
            problem = ex.Message;
            return LockState.Held;
        }
        catch (UnauthorizedAccessException ex)
        {
            problem = ex.Message;
            return LockState.Unusable;
        }
    }

    /// <summary>A second launch: asks the running EasyVocal to show its window. True when one was there. Never throws.</summary>
    public static bool WakeFirst(Names? names = null)
    {
        names ??= Names.Default;
        if (UsesMutex(names))
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(names.ShowEventName, out var signal)) return false;
                using (signal) signal.Set();
                return true;
            }
            catch (Exception) { return false; }
        }
        return Knock(names, show: true, WakeTimeout, waitForPipe: true);
    }

    /// <summary>
    /// Connects to the running EasyVocal's pipe; with <paramref name="show"/> asks it to show its window, else only looks
    /// (nothing happens there). Where the pipe is a file (<see cref="Names.PipeFile"/>: macOS, Linux) a missing file
    /// answers at once — or, with <paramref name="waitForPipe"/> (the lock is held: that EasyVocal may still be starting
    /// its listener), once it appears, within <paramref name="timeout"/> in all. True when an EasyVocal answered. Never throws.
    /// </summary>
    internal static bool Knock(Names names, bool show, TimeSpan timeout, bool waitForPipe = false)
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (names.PipeFile is { } file)
                while (!File.Exists(file))
                {
                    if (!waitForPipe || watch.Elapsed >= timeout) return false;
                    Thread.Sleep(20);
                }
            using var pipe = new NamedPipeClientStream(".", names.PipeName, PipeDirection.Out);
            pipe.Connect(Math.Max(1, (int)(timeout - watch.Elapsed).TotalMilliseconds));
            if (show)
            {
                pipe.WriteByte(ShowByte);
                pipe.Flush();
            }
            return true;
        }
        catch (Exception) { return false; } // nobody listens (or it is closing): nothing to show
    }

    /// <summary>Where .NET puts a pipe's Unix socket (macOS, Linux): the temp folder, "CoreFxPipe_" and the name.</summary>
    internal static string SocketPath(string pipeName) => Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);

    /// <summary>
    /// Another EasyVocal runs now (for <c>--update-now</c>, which must neither swap files under a running one nor refuse
    /// because of a stale lock). The same rules as <see cref="TryAcquire"/>: only an EasyVocal that answers its pipe
    /// counts (asked without showing its window), whether the lock was free, held or unusable.
    /// </summary>
    public static bool IsRunning(Names? names = null)
    {
        names ??= Names.Default;
        if (UsesMutex(names))
        {
            if (!Mutex.TryOpenExisting(names.MutexName, out var running)) return false;
            running.Dispose();
            return true;
        }
        var state = TakeLock(names, out var file, out _);
        file?.Dispose();
        bool held = state == LockState.Held;
        return Knock(names, show: false, held ? WakeTimeout : ProbeTimeout, waitForPipe: held);
    }

    /// <summary>
    /// A pipe name short enough for every OS: on macOS a Unix socket path is at most 104 characters, and .NET puts the
    /// socket in the temp folder (itself long on a Mac). "ev-" and 12 hex characters of the user's name.
    /// </summary>
    public static string ShortPipeName(string user)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("EasyVocal|" + user));
        return "ev-" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>Connections read at the same time; while they are all busy the next one waits.</summary>
    private const int MaxClients = 3;

    /// <summary>
    /// Listens until <paramref name="ct"/>; done once every pipe instance is closed. <see cref="Dispose"/> closes the
    /// socket and the connections being read itself, at once: on macOS/Linux an instance left open keeps the socket
    /// answering, and a launch right after this one quit (an update's restart, the e2e relaunch) would take it for a
    /// running EasyVocal.
    /// </summary>
    private async Task ListenAsync(CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(MaxClients, MaxClients);
        try { await ListenLoopAsync(slots, ct).ConfigureAwait(false); }
        finally
        {
            // The readers end as soon as their connection is closed (Dispose does that): never long.
            var deadline = DateTime.UtcNow + QuitWait;
            for (int i = 0; i < MaxClients; i++)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !await slots.WaitAsync(left).ConfigureAwait(false)) break;
            }
        }
    }

    /// <summary>How long quitting waits for the listener (it stops at once: its socket and connections are closed first).</summary>
    private static readonly TimeSpan QuitWait = TimeSpan.FromMilliseconds(500);

    private async Task ListenLoopAsync(SemaphoreSlim slots, CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                Stream connection;
                try { connection = unix != null ? await unix.AcceptAsync(ct).ConfigureAwait(false) : await AcceptPipeAsync(ct).ConfigureAwait(false); }
                catch
                {
                    slots.Release();
                    throw;
                }
                failures = 0;
                _ = ReadOneAsync(connection, slots, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) when (ct.IsCancellationRequested) { return; } // the socket was closed under the accept: quitting
            catch (Exception ex)
            {
                if (++failures >= MaxPipeFailures)
                {
                    AppLog.Error("The 'show the window' pipe failed " + failures + " times: a second launch won't find this one", ex);
                    return;
                }
                AppLog.Info("The 'show the window' pipe failed: " + ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(failures), ct).ConfigureAwait(false); } // back off a bit more each time
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Windows: the next connection on a named pipe instance of its own.</summary>
    private async Task<Stream> AcceptPipeAsync(CancellationToken ct)
    {
        // Several instances: the next one listens while a connection is still being read, so a client that connects and
        // says nothing never makes a real second launch wait (nor loses its wake).
        var server = new NamedPipeServerStream(names.PipeName, PipeDirection.In, MaxClients + 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A listening Unix socket at <c>path</c>, made on the first accept (again after a failure), removed on dispose — only
    /// while it is still ours: a later EasyVocal may have replaced a socket it took for a stale one, and deleting its file
    /// would make that one unreachable. Ours = the token written next to it (<c>path.owner</c>) is still the one we wrote.
    /// </summary>
    internal sealed class UnixListener(string path) : IDisposable
    {
        private readonly object gate = new();
        private readonly string token = Guid.NewGuid().ToString("N");
        private Socket? socket;
        private bool disposed;

        private string OwnerFile => path + ".owner";

        public async Task<Stream> AcceptAsync(CancellationToken ct)
        {
            Socket listening;
            lock (gate)
            {
                if (disposed) throw new OperationCanceledException(ct);
                if (socket == null)
                {
                    var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        // We hold the lock and nobody answered on it (TryAcquire): a socket file there is a stale one.
                        if (File.Exists(path)) File.Delete(path);
                        s.Bind(new UnixDomainSocketEndPoint(path));
                        s.Listen(16);
                        File.WriteAllText(OwnerFile, token);
                    }
                    catch
                    {
                        s.Dispose();
                        throw;
                    }
                    socket = s;
                }
                listening = socket;
            }
            try { return new NetworkStream(await listening.AcceptAsync(ct).ConfigureAwait(false), ownsSocket: true); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                Close();
                throw;
            }
        }

        /// <summary>True while the socket file at the path is the one this listener made.</summary>
        internal bool OwnsPath()
        {
            try { return File.Exists(path) && File.ReadAllText(OwnerFile) == token; }
            catch (Exception) { return false; }
        }

        private void Close()
        {
            lock (gate)
            {
                if (socket == null) return;
                socket.Dispose();
                socket = null;
                if (!OwnsPath()) return; // replaced by someone else's: theirs stays
                try { File.Delete(path); } catch (Exception) { /* gone already */ }
                try { File.Delete(OwnerFile); } catch (Exception) { /* gone already */ }
            }
        }

        /// <summary>Closes the socket at once (a pending accept ends) and removes its file if it is still ours.</summary>
        public void Dispose()
        {
            lock (gate) disposed = true;
            Close();
        }
    }

    /// <summary>Reads one connection's byte (at most <see cref="ReadTimeout"/>), then frees its slot. Never throws.</summary>
    private async Task ReadOneAsync(Stream server, SemaphoreSlim slots, CancellationToken ct)
    {
        bool tracked;
        lock (sync)
        {
            tracked = !disposed;
            if (tracked) connections.Add(server);
        }
        try
        {
            if (!tracked) return;
            var buffer = new byte[1];
            int read;
            using (var patience = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                patience.CancelAfter(ReadTimeout);
                try { read = await server.ReadAsync(buffer, patience.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { read = 0; } // connected and never wrote, or quitting
            }
            if (read == 1 && buffer[0] == ShowByte && !ct.IsCancellationRequested) RaiseShow(); // a mere look (IsRunning) shows nothing
        }
        catch (Exception) when (ct.IsCancellationRequested) { /* closed under the read: quitting */ }
        catch (Exception ex) { AppLog.Info("A 'show the window' connection failed: " + ex.Message); }
        finally
        {
            lock (sync) connections.Remove(server);
            server.Dispose();
            try { slots.Release(); } catch (ObjectDisposedException) { /* the listener has stopped */ }
        }
    }

    /// <summary>
    /// Lets go: the socket and every connection are closed right here, before the lock is let go (see
    /// <see cref="ListenAsync"/>), so the listener stops at once; this waits for it <see cref="QuitWait"/> at most.
    /// </summary>
    public void Dispose()
    {
        Stream[] open;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            open = connections.ToArray();
        }
        stop.Cancel();
        unix?.Dispose();
        foreach (var c in open)
        {
            try { c.Dispose(); } catch (Exception) { /* closing anyway */ }
        }
        try { Listening?.Wait(QuitWait); }
        catch (Exception) { /* it logged its own trouble */ }
        showWait?.Unregister(null);
        showSignal?.Dispose();
        if (mutex != null)
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
            mutex.Dispose();
        }
        lockFile?.Dispose();
    }
}
