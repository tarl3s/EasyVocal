using Avalonia.Threading;

namespace EasyVocal;

/// <summary>
/// The UI thread as <see cref="AppController"/> sees it: post work to it, ask whether we are on it, and timers that tick
/// on it. The app uses Avalonia's dispatcher (<see cref="AvaloniaUiDispatcher"/>); a test can pass its own.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Runs <paramref name="action"/> on the UI thread later (never waits for it).</summary>
    void Post(Action action);

    /// <summary>A stopped timer whose <paramref name="tick"/> runs on the UI thread every <paramref name="interval"/>.</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick);
}

/// <summary>A timer made by <see cref="IUiDispatcher.CreateTimer"/>.</summary>
public interface IUiTimer
{
    void Start();

    void Stop();
}

/// <summary>Avalonia's UI thread (background priority, like the WPF timers the controller had).</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static AvaloniaUiDispatcher Instance { get; } = new();

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(interval, tick);

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherTimer timer;

        public Timer(TimeSpan interval, Action tick)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
            timer.Tick += (_, _) => tick();
        }

        public void Start() => timer.Start();

        public void Stop() => timer.Stop();
    }
}
