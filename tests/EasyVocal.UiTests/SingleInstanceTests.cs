using EasyVocal.Tests.Helpers;

namespace EasyVocal.UiTests;

/// <summary>
/// One EasyVocal at a time, on names of the tests' own (never the person's EasyVocal): the portable lock file + pipe
/// (macOS/Linux, run here on every OS) and Windows' mutex + event.
/// </summary>
public class SingleInstanceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "EV_UiSingle_" + Guid.NewGuid().ToString("N"));

    public SingleInstanceTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        TestFolders.Delete(folder);
    }

    private SingleInstance.Names Names(bool portable)
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        return new SingleInstance.Names("EasyVocal.Test.Mutex." + id, "EasyVocal.Test.Show." + id,
            Path.Combine(folder, id + ".lock"), "ev-t" + id, portable);
    }

    [Fact]
    public void Portable_SecondLaunchGetsNothing_AndWakesTheFirst_EvenBeforeItListens()
    {
        var names = Names(portable: true);
        using var first = SingleInstance.TryAcquire(names);
        Assert.NotNull(first);
        Assert.False(first!.WithoutLock);

        // A second launch before the first set its handler: the wake is kept and handed over later.
        Assert.Null(SingleInstance.TryAcquire(names));
        using var shown = new ManualResetEventSlim();
        first.OnShowRequested(shown.Set);
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)), "the early wake was replayed");

        shown.Reset();
        Assert.True(SingleInstance.WakeFirst(names));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)), "a later wake reaches the handler (listener " + first.Listening?.Status +
            ", socket there: " + (names.PipeFile is { } f && File.Exists(f)) + ", " + first.Listening?.Exception?.GetBaseException().Message + ")");
        Assert.True(SingleInstance.IsRunning(names));
    }

    [Fact]
    public void Portable_FreedLock_CanBeTakenAgain()
    {
        var names = Names(portable: true);
        SingleInstance.TryAcquire(names)!.Dispose();
        using var again = SingleInstance.TryAcquire(names);
        Assert.NotNull(again);
        Assert.False(again!.WithoutLock);
    }

    [Fact]
    public void Portable_ALockNobodyAnswersFor_IsNotARunningEasyVocal()
    {
        var names = Names(portable: true);
        // Something else holds the file (a stale lock, another program) and no EasyVocal listens on the pipe.
        using var stranger = new FileStream(names.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var mine = SingleInstance.TryAcquire(names);
        Assert.NotNull(mine);
        Assert.True(mine!.WithoutLock);
        // It still answers a later launch.
        using var shown = new ManualResetEventSlim();
        mine.OnShowRequested(shown.Set);
        Assert.True(SingleInstance.WakeFirst(names));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Windows_MutexAndEvent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var names = Names(portable: false);
        using var first = SingleInstance.TryAcquire(names);
        Assert.NotNull(first);
        using var shown = new ManualResetEventSlim();
        first!.OnShowRequested(shown.Set);
        Assert.Null(SingleInstance.TryAcquire(names));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)), "the second launch woke the first");
        Assert.True(SingleInstance.IsRunning(names));
    }

    /// <summary>The portable names with a stand-in for the pipe's socket file (the test makes it appear), on any OS.</summary>
    private SingleInstance.Names WithPipeFile(out string pipeFile)
    {
        var names = Names(portable: true);
        pipeFile = Path.Combine(folder, names.PipeName + ".sock");
        return names with { PipeFile = pipeFile };
    }

    [Fact]
    public void Portable_AHeldLock_WaitsForTheRunningEasyVocalsPipeToAppear()
    {
        var names = WithPipeFile(out var pipeFile);
        using var held = new FileStream(names.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        int got = -1;
        // The first EasyVocal took the lock a moment ago and is only now starting its listener (a thread of its own:
        // the thread pool is busy while the test projects start).
        var listener = new Thread(() =>
        {
            Thread.Sleep(200);
            using var server = new System.IO.Pipes.NamedPipeServerStream(names.PipeName, System.IO.Pipes.PipeDirection.In, 1);
            File.WriteAllText(pipeFile, "");
            server.WaitForConnection();
            got = server.ReadByte();
        }) { IsBackground = true };
        listener.Start();
        Assert.Null(SingleInstance.TryAcquire(names)); // a quick double launch: the second one leaves
        Assert.True(listener.Join(TimeSpan.FromSeconds(10)), "the pretend first EasyVocal got the knock");
        Assert.Equal(1, got); // and asked the first to show its window
    }

    [Fact]
    public void Portable_WithoutTheLock_NeverTakesOverAPipeThatIsThere()
    {
        var names = WithPipeFile(out var pipeFile);
        using var held = new FileStream(names.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(pipeFile, ""); // a socket there that doesn't answer (its EasyVocal is busy, or it is someone else's)
        using var mine = SingleInstance.TryAcquire(names);
        Assert.NotNull(mine);
        Assert.True(mine!.WithoutLock);
        Assert.Null(mine.Listening); // listening would replace that socket

        // No pipe file: a lock nobody answers for still gets a listener.
        File.Delete(pipeFile);
        using var second = SingleInstance.TryAcquire(names);
        Assert.NotNull(second!.Listening);
    }

    [Fact]
    public void Portable_Quitting_IsQuick_EvenWithAClientConnectedThatSaysNothing()
    {
        var names = Names(portable: true);
        var first = SingleInstance.TryAcquire(names)!;
        Assert.NotNull(first);
        using var silent = new System.IO.Pipes.NamedPipeClientStream(".", names.PipeName, System.IO.Pipes.PipeDirection.Out);
        silent.Connect(2000); // connected, and being read
        Thread.Sleep(200);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        first.Dispose();
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 1000, "quitting took " + watch.ElapsedMilliseconds + " ms");
        Assert.True(first.Listening!.IsCompleted, "the listener stopped before the lock was let go");
        Assert.False(SingleInstance.IsRunning(names));
    }

    [Fact]
    public async Task UnixSocket_ThatIsNoLongerOurs_IsNeverDeleted()
    {
        // macOS/Linux only, where the listener runs: Windows deletes a socket file only once its last handle closes.
        if (OperatingSystem.IsWindows()) return;
        // Short: a Unix socket path is at most 104 characters on macOS.
        var path = Path.Combine(Path.GetTempPath(), "evs" + Guid.NewGuid().ToString("N")[..8]);
        using var cts = new CancellationTokenSource();
        var a = new SingleInstance.UnixListener(path);
        var b = new SingleInstance.UnixListener(path);
        try
        {
            var acceptA = a.AcceptAsync(cts.Token);
            Assert.True(a.OwnsPath());
            // A later EasyVocal took that socket for a stale one and made its own at the same path.
            var acceptB = b.AcceptAsync(cts.Token);
            Assert.False(a.OwnsPath());
            Assert.True(b.OwnsPath());

            a.Dispose(); // the first one quits: the file is the later one's now, and stays
            Assert.True(File.Exists(path));
            await Assert.ThrowsAnyAsync<Exception>(() => acceptA.WaitAsync(TimeSpan.FromSeconds(5)));
            using (var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
            {
                client.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path)); // the later one still answers
                using var accepted = await acceptB.WaitAsync(TimeSpan.FromSeconds(5));
            }

            b.Dispose(); // its own: removed, with its token
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".owner"));
        }
        finally
        {
            a.Dispose();
            b.Dispose();
            try { File.Delete(path); } catch (Exception) { /* gone */ }
            try { File.Delete(path + ".owner"); } catch (Exception) { /* gone */ }
        }
    }

    [Fact]
    public void PipeName_IsShortAndPerUser()
    {
        var a = SingleInstance.ShortPipeName("alice");
        Assert.Matches("^ev-[0-9a-f]{12}$", a);
        Assert.NotEqual(a, SingleInstance.ShortPipeName("bob"));
        Assert.Equal(a, SingleInstance.ShortPipeName("alice"));
    }

    [Fact]
    public void Portable_AFirstLaunch_DoesNotWaitForAnswersNobodyGives()
    {
        var names = Names(portable: true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var first = SingleInstance.TryAcquire(names);
        watch.Stop();
        Assert.NotNull(first);
        Assert.False(first!.WithoutLock);
        // Only a quick look (none at all where the pipe is a socket file), never the 2 s a held lock gets.
        Assert.True(watch.ElapsedMilliseconds < 400, "took " + watch.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void Portable_IsRunning_AsksWithoutShowingTheWindow_AndIgnoresAStaleLock()
    {
        var names = Names(portable: true);
        using (var stranger = new FileStream(names.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.False(SingleInstance.IsRunning(names)); // a lock nobody answers for: no EasyVocal to wait for
        Assert.False(SingleInstance.IsRunning(names));

        using var first = SingleInstance.TryAcquire(names);
        Assert.NotNull(first);
        int shows = 0;
        first!.OnShowRequested(() => Interlocked.Increment(ref shows));
        Assert.True(SingleInstance.IsRunning(names));
        Assert.True(SingleInstance.IsRunning(names));
        Thread.Sleep(300);
        Assert.Equal(0, Volatile.Read(ref shows));
    }

    [Fact]
    public void Portable_AClientThatNeverWrites_CantBlockTheListener()
    {
        var names = Names(portable: true);
        using var first = SingleInstance.TryAcquire(names);
        Assert.NotNull(first);
        using var shown = new ManualResetEventSlim();
        first!.OnShowRequested(shown.Set);
        using var silent = new System.IO.Pipes.NamedPipeClientStream(".", names.PipeName, System.IO.Pipes.PipeDirection.Out);
        silent.Connect(2000); // and then says nothing, and stays connected
        var until = DateTime.UtcNow.AddSeconds(8);
        bool woke = false;
        while (!woke && DateTime.UtcNow < until) woke = SingleInstance.WakeFirst(names);
        Assert.True(woke, "a later launch got through");
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
    }
}
