using System.Diagnostics;
using System.Runtime.InteropServices;
using EasyVocal.Platform.Unix;

namespace EasyVocal.Tests.Platforms;

/// <summary>
/// How a PortAudio stream closes, on pretend PortAudio calls (any OS, no device): never from the thread that disposes it,
/// never while its audio thread may be stopping it (that froze macOS), "stopped" before Pa_CloseStream, and a callback
/// for something that isn't a stream of ours is silence and paComplete.
/// </summary>
public class PortAudioCloseTests
{
    private const int Continue = 0, Complete = 1;

    /// <summary>For the waits a test answers itself: a slow machine never makes its stream look dead.</summary>
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    /// <summary>The PortAudio calls, recorded; "stopped" only after a stop call, as macOS's PortAudio after paComplete.</summary>
    private sealed class PretendCalls : IPaStreamCalls
    {
        private readonly object sync = new();
        private readonly List<string> made = new();
        private bool stopped;

        /// <summary>What the stream says while not stopped (1 = active).</summary>
        public int Active = 1;

        /// <summary>Runs inside Abort (a callback that comes while the stream is being aborted).</summary>
        public Action? DuringAbort;

        /// <summary>Whether PortAudio has said the stream finished (read at each call, for the record).</summary>
        public Func<bool> Finished = () => false;

        public string[] Made
        {
            get { lock (sync) return made.ToArray(); }
        }

        private void Note(string call)
        {
            lock (sync) made.Add(call + (Finished() ? " (finished)" : "") + (stopped ? " (stopped)" : ""));
        }

        public int Start(IntPtr stream) { Note("start"); return 0; }

        public int Abort(IntPtr stream)
        {
            Note("abort");
            DuringAbort?.Invoke();
            lock (sync) stopped = true;
            return 0;
        }

        public int Close(IntPtr stream) { Note("close"); return 0; }
        public int IsActive(IntPtr stream) { lock (sync) return stopped ? 0 : Active; }
        public int IsStopped(IntPtr stream) { lock (sync) return stopped ? 1 : 0; }
    }

    private sealed unsafe class PretendStream : PortAudioStream
    {
        public PretendStream(PortAudioBackend backend, PretendCalls calls) : base(backend)
        {
            AttachForTests(calls, outputChannels: 2);
            calls.Finished = () => finished;
        }

        public volatile bool finished;

        public void SayFinished()
        {
            finished = true;
            FinishedForTests();
        }

        protected override void OnAudio(float* input, float* output, int frames)
        {
            for (int i = 0; i < frames * 2; i++) output[i] = 0.5f;
        }

        protected override void Fail(Exception why) { }
    }

    private static float[] Ones(int frames) => Enumerable.Repeat(1f, frames * 2).ToArray();

    [Fact]
    public async Task Closing_NeverWaitsForTheLock_AndClosesOnlyOnceTheAudioThreadStoppedIt()
    {
        using var backend = new PortAudioBackend();
        var calls = new PretendCalls();
        var s = new PretendStream(backend, calls) { AnswerWait = Patient, StopWait = Patient };
        s.Start();
        var block = Ones(480);
        Assert.Equal(Continue, s.CallbackForTests(block, 480));
        Assert.All(block, v => Assert.Equal(0.5f, v));

        // Another thread holds the backend's lock (a device list being read, another stream opening).
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (backend.Sync)
            {
                holding.Set();
                release.Wait();
            }
        }) { IsBackground = true };
        holder.Start();
        Assert.True(holding.Wait(5000));
        var watch = Stopwatch.StartNew();
        s.Dispose(); // the UI thread: returns at once
        Assert.True(watch.ElapsedMilliseconds < 500, "Dispose took " + watch.ElapsedMilliseconds + " ms");
        release.Set();
        Assert.True(holder.Join(5000));

        // The next block: silence, and paComplete — PortAudio stops it on its own audio thread now.
        block = Ones(480);
        Assert.Equal(Complete, s.CallbackForTests(block, 480));
        Assert.All(block, v => Assert.Equal(0f, v));
        Thread.Sleep(300);
        Assert.Equal(new[] { "start" }, calls.Made); // nothing from outside while its audio thread may be stopping it
        Assert.False(s.Closed.IsCompleted);
        Assert.False(backend.WaitForClosing(TimeSpan.FromMilliseconds(50))); // an open would wait for it

        s.SayFinished(); // PortAudio: the stream stopped
        await s.Closed.WaitAsync(TimeSpan.FromSeconds(5)); // closed once PortAudio said it stopped
        Assert.Equal("completed", s.CloseRoute);
        // Made "stopped" (nothing left to stop), then closed while stopped: Pa_CloseStream never aborts it itself.
        Assert.Equal(new[] { "start", "abort (finished)", "close (finished) (stopped)" }, calls.Made);
        Assert.True(backend.WaitForClosing(TimeSpan.Zero));
    }

    [Fact]
    public async Task AStreamWhoseCallbackStoppedComing_IsAbortedFromHere_AndItsCallbackThenNeverCompletes()
    {
        using var backend = new PortAudioBackend();
        var calls = new PretendCalls();
        var s = new PretendStream(backend, calls) { AnswerWait = TimeSpan.FromMilliseconds(100) };
        int duringAbort = -1;
        float[] block = Ones(480);
        // A callback that comes while it is being aborted: silence, and paContinue (never a second stop of its own).
        calls.DuringAbort = () => duringAbort = s.CallbackForTests(block, 480);
        s.Start();
        s.Dispose(); // a device gone: no callback answers
        await s.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("aborted", s.CloseRoute);
        Assert.Equal(Continue, duringAbort);
        Assert.All(block, v => Assert.Equal(0f, v));
        Assert.Equal(new[] { "start", "abort", "close (stopped)" }, calls.Made);
    }

    [Fact]
    public async Task AStreamThatNeverStops_IsLeftOpen_NeverStoppedFromHere()
    {
        using var backend = new PortAudioBackend();
        var calls = new PretendCalls();
        var s = new PretendStream(backend, calls) { AnswerWait = Patient, StopWait = TimeSpan.FromMilliseconds(200) };
        s.Start();
        s.Dispose();
        Assert.Equal(Complete, s.CallbackForTests(Ones(480), 480));
        await s.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("left open", s.CloseRoute);
        Assert.Equal(new[] { "start" }, calls.Made);
        Assert.True(backend.WaitForClosing(TimeSpan.Zero)); // not closing any more (and never restarted under, see Closed)
    }

    [Fact]
    public async Task AStreamThatDoesntSayItStopped_IsClosedOnceInactive()
    {
        using var backend = new PortAudioBackend();
        var calls = new PretendCalls { Active = 0 }; // its callback completed and it went quiet, but no word from PortAudio
        var s = new PretendStream(backend, calls) { AnswerWait = Patient, StopWait = TimeSpan.FromMilliseconds(200) };
        s.Start();
        s.Dispose();
        Assert.Equal(Complete, s.CallbackForTests(Ones(480), 480));
        await s.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("completed", s.CloseRoute);
        Assert.Equal(new[] { "start", "abort", "close (stopped)" }, calls.Made);
    }

    [Fact]
    public async Task ANeverStartedStream_IsClosedWithoutWaiting()
    {
        using var backend = new PortAudioBackend();
        var calls = new PretendCalls();
        var s = new PretendStream(backend, calls) { AnswerWait = Patient };
        s.Dispose();
        await s.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("never started", s.CloseRoute);
        Assert.Equal(new[] { "close" }, calls.Made);
        Assert.Throws<ObjectDisposedException>(() => s.Start());
    }

    [Fact]
    public void ACallbackForSomethingElse_IsSilence_AndComplete()
    {
        var stranger = GCHandle.Alloc(new object());
        try
        {
            var block = Ones(480);
            Assert.Equal(Complete, PortAudioStream.CallbackForTests(GCHandle.ToIntPtr(stranger), 2, block, 480));
            Assert.All(block, v => Assert.Equal(0f, v));
        }
        finally { stranger.Free(); }

        var none = Ones(480);
        Assert.Equal(Complete, PortAudioStream.CallbackForTests(IntPtr.Zero, 2, none, 480));
        Assert.All(none, v => Assert.Equal(0f, v));
    }
}
