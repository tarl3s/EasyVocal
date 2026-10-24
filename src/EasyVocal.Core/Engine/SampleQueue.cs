using System;

namespace EasyVocal.Core.Engine;

/// <summary>
/// The samples waiting for a playback device: the capture thread writes, the device's render thread reads.
/// Everything is preallocated; the lock only guards a copy of a few hundred samples, never any DSP.
/// <para>
/// Mic and device clocks never run at exactly the same speed, so the fill drifts. Two things keep that inaudible:
/// <see cref="TrimTo"/> drops the oldest audio down to a target fill with a short crossfade at the cut (no click,
/// no whole-backlog skip), and when the queue runs dry the last samples fade out, then playback waits until
/// <c>primeSamples</c> are queued again (a small cushion) and fades back in.
/// </para>
/// </summary>
public sealed class SampleQueue
{
    /// <summary>Reads to wait for the cushion before playing what is there anyway (the end of a sound may be shorter).</summary>
    private const int MaxPrimeWaits = 2;

    private readonly object sync = new();
    private readonly float[] ring;
    private readonly int fadeLength;
    private readonly int primeSamples;
    private int head;          // index of the oldest sample
    private int count;
    private bool primed;       // false: output silence until primeSamples are queued
    private int primeWaits;    // reads spent waiting for the cushion (a short tail must still play)
    private int fadeInPos;     // < fadeLength while fading in after a dry spell

    /// <param name="capacity">Most samples it holds; a write beyond that loses the newest samples.</param>
    /// <param name="fadeLength">Length of the crossfade at a cut and of the fades around a dry spell.</param>
    /// <param name="primeSamples">How much must be queued before playback (re)starts.</param>
    public SampleQueue(int capacity, int fadeLength, int primeSamples)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        ring = new float[capacity];
        this.fadeLength = Math.Max(1, fadeLength);
        this.primeSamples = Math.Clamp(primeSamples, 0, capacity);
        fadeInPos = this.fadeLength;
    }

    /// <summary>Samples queued and not yet read.</summary>
    public int Count
    {
        get { lock (sync) return count; }
    }

    /// <summary>Capture thread: queues samples (what doesn't fit is dropped).</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        lock (sync)
        {
            int n = Math.Min(samples.Length, ring.Length - count);
            int tail = (head + count) % ring.Length;
            int first = Math.Min(n, ring.Length - tail);
            samples[..first].CopyTo(ring.AsSpan(tail));
            samples.Slice(first, n - first).CopyTo(ring);
            count += n;
        }
    }

    /// <summary>
    /// Render thread: fills <paramref name="dest"/> entirely, with silence where nothing is queued. Returns how many
    /// real samples it gave.
    /// </summary>
    public int Read(Span<float> dest)
    {
        lock (sync)
        {
            if (!primed)
            {
                if (count == 0 || (count < primeSamples && primeWaits < MaxPrimeWaits))
                {
                    if (count > 0) primeWaits++;
                    dest.Clear();
                    return 0;
                }
                primed = true;
                primeWaits = 0;
            }
            int n = Math.Min(dest.Length, count);
            int first = Math.Min(n, ring.Length - head);
            ring.AsSpan(head, first).CopyTo(dest);
            ring.AsSpan(0, n - first).CopyTo(dest[first..]);
            head = (head + n) % ring.Length;
            count -= n;

            for (int i = 0; i < n && fadeInPos < fadeLength; i++, fadeInPos++)
                dest[i] *= (fadeInPos + 1) / (float)(fadeLength + 1);

            if (n < dest.Length)
            {
                // Ran dry: fade out what we have instead of stopping dead, then wait for a cushion again.
                int f = Math.Min(fadeLength, n);
                for (int k = 0; k < f; k++) dest[n - f + k] *= (f - k) / (float)(f + 1);
                dest[n..].Clear();
                primed = false;
                fadeInPos = 0;
            }
            return n;
        }
    }

    /// <summary>
    /// Throws away the oldest samples so only <paramref name="keep"/> remain. The audio about to play fades into the
    /// audio after the cut over a short crossfade, so the jump is not heard as a click.
    /// </summary>
    public void TrimTo(int keep)
    {
        lock (sync)
        {
            keep = Math.Max(0, keep);
            if (count <= keep) return;
            int drop = count - keep;
            int f = Math.Min(fadeLength, keep);
            // Downwards: each write lands at or after any index still to be read, so nothing is read after it changed.
            for (int k = f - 1; k >= 0; k--)
            {
                float g = (k + 1) / (float)(f + 1);
                int from = (head + k) % ring.Length;
                int to = (head + drop + k) % ring.Length;
                ring[to] = ring[from] * (1f - g) + ring[to] * g;
            }
            head = (head + drop) % ring.Length;
            count = keep;
        }
    }

    /// <summary>Forgets everything queued.</summary>
    public void Clear()
    {
        lock (sync)
        {
            head = 0;
            count = 0;
            primed = false;
            primeWaits = 0;
            fadeInPos = fadeLength;
        }
    }
}
