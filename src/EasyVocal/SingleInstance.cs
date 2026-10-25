using System.IO;
using System.Runtime.Versioning;
using System.IO.Pipes;
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
    }

    /// <summary>After this many pipe failures in a row the listener gives up (a second launch then just starts).</summary>
    private const int MaxPipeFailures = 5;
    private static readonly TimeSpan WakeTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Names names;
    private readonly Mutex? mutex;
    private readonly EventWaitHandle? showSignal;
    private readonly RegisteredWaitHandle? showWait;
    private readonly FileStream? lockFile;
    private readonly CancellationTokenSource stop = new();
    private readonly object sync = new();
    private Action? show;
    private bool pendingShow, disposed;

    private SingleInstance(Names names, Mutex mutex, EventWaitHandle showSignal)
    {
        this.names = names;
        this.mutex = mutex;
        this.showSignal = showSignal;
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => RaiseShow(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private SingleInstance(Names names, FileStream? lockFile)
    {
        this.names = names;
        this.lockFile = lockFile;
        Listening = ListenAsync(stop.Token);
    }

    /// <summary>The pipe listener (portable path); done when it stopped.</summary>
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
        FileStream file;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(names.LockPath)!);
            file = new FileStream(names.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by the running EasyVocal — or a lock we can't use. Only an answer on the pipe says another one runs.
            if (WakeFirst(names)) return null;
            AppLog.Info("The single-instance lock couldn't be taken and no EasyVocal answered: running without it (" + ex.Message + ")");
            return new SingleInstance(names, null);
        }
        // Locking may do nothing here (NFS, DOTNET_SYSTEM_IO_DISABLEFILELOCKING): an EasyVocal that answers wins.
        if (WakeFirst(names))
        {
            file.Dispose();
            return null;
        }
        return new SingleInstance(names, file);
    }

    /// <summary>A second launch: asks the running EasyVocal to show its window. True when one was there. Never throws.</summary>
    public static bool WakeFirst(Names? names = null)
    {
        names ??= Names.Default;
        try
        {
            if (UsesMutex(names))
            {
                if (!EventWaitHandle.TryOpenExisting(names.ShowEventName, out var signal)) return false;
                using (signal) signal.Set();
                return true;
            }
            using var pipe = new NamedPipeClientStream(".", names.PipeName, PipeDirection.Out);
            pipe.Connect((int)WakeTimeout.TotalMilliseconds);
            pipe.WriteByte(1);
            pipe.Flush();
            return true;
        }
        catch (Exception) { return false; } // nobody listens (or it is closing): nothing to show
    }

    /// <summary>Another EasyVocal runs now (for <c>--update-now</c>).</summary>
    public static bool IsRunning(Names? names = null)
    {
        names ??= Names.Default;
        if (UsesMutex(names))
        {
            if (!Mutex.TryOpenExisting(names.MutexName, out var running)) return false;
            running.Dispose();
            return true;
        }
        try
        {
            using var file = new FileStream(names.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
        return false;
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

    private async Task ListenAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(names.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var buffer = new byte[1];
                _ = await server.ReadAsync(buffer, ct).ConfigureAwait(false);
                failures = 0;
                RaiseShow();
            }
            catch (OperationCanceledException) { return; }
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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
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
