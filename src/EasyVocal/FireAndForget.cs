namespace EasyVocal;

/// <summary>
/// Work the UI starts and doesn't wait for (a dialog opened from a click, a file loaded for an editor): watched, so a
/// failure is logged and reported (<see cref="Failed"/>: the smoke and e2e tests count it) the moment it happens instead
/// of whenever the garbage collector finds an unobserved task, and the tests can wait for what is still running.
/// </summary>
internal static class FireAndForget
{
    private static readonly object sync = new();
    private static readonly HashSet<Task> running = new();

    /// <summary>A watched task failed (raised on the thread that finished it).</summary>
    public static event Action<Exception>? Failed;

    /// <summary>Watches <paramref name="task"/>; <paramref name="what"/> names it in the log.</summary>
    public static void Watch(Task task, string what)
    {
        if (task.IsCompletedSuccessfully) return;
        lock (sync) running.Add(task);
        task.ContinueWith(t =>
        {
            lock (sync) running.Remove(t);
            if (t.Exception is not { } ex) return; // done, or cancelled (a cancelled dialog is not a failure)
            var error = ex.InnerExceptions.Count == 1 ? ex.InnerException! : ex;
            AppLog.Error(what + " failed", error);
            try { Failed?.Invoke(error); }
            catch (Exception) { /* reporting must never fail the task's continuation */ }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>How many watched tasks are still running.</summary>
    public static int Running
    {
        get { lock (sync) return running.Count; }
    }

    /// <summary>
    /// Waits until every watched task has finished, or <paramref name="limit"/> passed (a window still open keeps its
    /// task running). True when none is left.
    /// </summary>
    public static async Task<bool> SettleAsync(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (true)
        {
            Task[] now;
            lock (sync) now = running.ToArray();
            if (now.Length == 0) return true;
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return false;
            var all = Task.WhenAll(now);
            _ = all.ContinueWith(t => _ = t.Exception, CancellationToken.None, // reported by Watch already: only observed here
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await Task.WhenAny(all, Task.Delay(left));
        }
    }
}
