using System.IO;
using System.IO.Pipes;
using EasyVocal.Core.Storage;

namespace EasyVocal;

/// <summary>
/// One EasyVocal per person: the first launch owns the lock; a later launch tells it to show its window and leaves.
/// Windows: a named mutex and a named event (as 0.2.0). Elsewhere: a lock file in the data folder held open with no
/// sharing, and a local pipe (a Unix socket under the hood) the second launch writes to.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "EasyVocal.SingleInstance";
    private const string ShowEventName = "EasyVocal.ShowWindow";

    private readonly Mutex? mutex;
    private readonly EventWaitHandle? showSignal;
    private readonly RegisteredWaitHandle? showWait;
    private readonly FileStream? lockFile;
    private readonly CancellationTokenSource stop = new();
    private Action? show;
    private bool disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle showSignal)
    {
        this.mutex = mutex;
        this.showSignal = showSignal;
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => RaiseShow(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private SingleInstance(FileStream lockFile)
    {
        this.lockFile = lockFile;
        _ = ListenAsync(stop.Token);
    }

    /// <summary>The first instance's handler for "show your window" (raised on a background thread).</summary>
    public void OnShowRequested(Action handler) => show = handler;

    private void RaiseShow()
    {
        try { show?.Invoke(); }
        catch (Exception ex) { AppLog.Error("Showing the window for a second launch failed", ex); }
    }

    /// <summary>The lock when this is the first EasyVocal, else null (another one runs: <see cref="WakeFirst"/>).</summary>
    public static SingleInstance? TryAcquire()
    {
        if (OperatingSystem.IsWindows())
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var m = new Mutex(true, MutexName, out bool first);
            if (first) return new SingleInstance(m, signal);
            m.Dispose();
            signal.Dispose();
            return null;
        }
        try
        {
            Directory.CreateDirectory(SettingsStore.DefaultFolder);
            var file = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(file);
        }
        catch (IOException)
        {
            return null; // held by the running EasyVocal
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Error("The single-instance lock can't be made: going on without it", ex);
            return new SingleInstance(new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
    }

    /// <summary>A second launch: asks the running EasyVocal to show its window. Never throws.</summary>
    public static void WakeFirst()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var signal))
                    using (signal) signal.Set();
                return;
            }
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            pipe.WriteByte(1);
        }
        catch (Exception) { /* it may be closing: nothing to show */ }
    }

    /// <summary>Another EasyVocal runs now (for <c>--update-now</c>).</summary>
    public static bool IsRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!Mutex.TryOpenExisting(MutexName, out var running)) return false;
            running.Dispose();
            return true;
        }
        try
        {
            using var file = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string LockPath => Path.Combine(SettingsStore.DefaultFolder, "instance.lock");

    /// <summary>One name per person (the pipe lives in the temp folder on Unix).</summary>
    private static string PipeName => "EasyVocal.ShowWindow." + Environment.UserName;

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var buffer = new byte[1];
                _ = await server.ReadAsync(buffer, ct).ConfigureAwait(false);
                RaiseShow();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLog.Error("The 'show the window' pipe failed", ex);
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
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
