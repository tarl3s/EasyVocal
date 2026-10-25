using EasyVocal.Core.Logging;

namespace EasyVocal.Ai.Engine;

/// <summary>
/// Loaded models shared by every stream (the voice, the sounds through it, the benchmark), by key ("content:int8:CPU"…).
/// Each user holds a lease; a model nobody leases stays loaded for a while (switching back to an AI voice is
/// instant) and is unloaded by <see cref="Trim"/> after <see cref="KeepIdle"/>. Thread-safe.
/// </summary>
public sealed class ModelCache : IDisposable
{
    private sealed class Entry
    {
        public object? Value;
        public int Refs;
        public DateTime LastUsedUtc;
        public readonly object LoadGate = new();
    }

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private bool disposed;

    public TimeSpan KeepIdle { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Loads (once) and leases a model. The value is disposed when unloaded if it is IDisposable.</summary>
    public T Acquire<T>(string key, Func<T> load, List<string> leased) where T : class
    {
        Entry e;
        lock (entries)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(key, out e!)) entries[key] = e = new Entry();
            e.Refs++;
            e.LastUsedUtc = DateTime.UtcNow;
        }
        try
        {
            lock (e.LoadGate)
            {
                if (e.Value == null)
                {
                    e.Value = load();
                    AppLog.Info("AI: loaded " + key);
                }
            }
            leased.Add(key);
            return (T)e.Value;
        }
        catch
        {
            Release(key);
            throw;
        }
    }

    /// <summary>Leases models that are already loaded again (a follower stream); false when one of them isn't any more.</summary>
    public bool TryAddRefs(IReadOnlyList<string> keys)
    {
        lock (entries)
        {
            foreach (var k in keys)
                if (!entries.TryGetValue(k, out var e) || e.Value == null) return false;
            foreach (var k in keys) { entries[k].Refs++; entries[k].LastUsedUtc = DateTime.UtcNow; }
            return true;
        }
    }

    public void Release(string key)
    {
        lock (entries)
        {
            if (entries.TryGetValue(key, out var e))
            {
                e.Refs = Math.Max(0, e.Refs - 1);
                e.LastUsedUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>A lease over several keys, released once.</summary>
    public IDisposable Lease(IReadOnlyList<string> keys) => new KeysLease(this, keys);

    private sealed class KeysLease : IDisposable
    {
        private ModelCache? cache;
        private readonly IReadOnlyList<string> keys;
        public KeysLease(ModelCache cache, IReadOnlyList<string> keys) { this.cache = cache; this.keys = keys; }
        public void Dispose()
        {
            var c = Interlocked.Exchange(ref cache, null);
            if (c == null) return;
            foreach (var k in keys) c.Release(k);
        }
    }

    /// <summary>How many leases a key has (tests).</summary>
    public int RefsOf(string key)
    {
        lock (entries) return entries.TryGetValue(key, out var e) ? e.Refs : 0;
    }

    public bool IsLoaded(string key)
    {
        lock (entries) return entries.TryGetValue(key, out var e) && e.Value != null;
    }

    /// <summary>Unloads what nobody has leased for <see cref="KeepIdle"/> (everything unleased with <paramref name="all"/>).</summary>
    public void Trim(bool all = false)
    {
        var drop = new List<object>();
        lock (entries)
        {
            var now = DateTime.UtcNow;
            foreach (var (k, e) in entries.ToList())
            {
                if (e.Refs > 0 || (!all && now - e.LastUsedUtc < KeepIdle)) continue;
                entries.Remove(k);
                if (e.Value != null) drop.Add(e.Value);
                AppLog.Info("AI: unloaded " + k);
            }
        }
        foreach (var v in drop) (v as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        List<object> drop;
        lock (entries)
        {
            if (disposed) return;
            disposed = true;
            drop = entries.Values.Where(e => e.Value != null).Select(e => e.Value!).ToList();
            entries.Clear();
        }
        foreach (var v in drop) (v as IDisposable)?.Dispose();
    }
}
