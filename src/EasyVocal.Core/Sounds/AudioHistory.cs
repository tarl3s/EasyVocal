using System;
using System.Collections.Generic;

namespace EasyVocal.Core.Sounds;

/// <summary>One version of the sound in the editor. <see cref="IsOriginal"/>: these are the original file's samples.</summary>
public sealed record AudioState(float[] Samples, bool IsOriginal);

/// <summary>
/// The sound editor's Undo / Redo. Every edit pushes the new version; Undo and Redo move between them. The oldest
/// versions are forgotten past <see cref="MaxSteps"/> undo steps or when all the kept versions together pass
/// <see cref="MaxSamples"/> (a 10-minute sound is 115 MB per version), but one undo step is always kept.
/// </summary>
public sealed class AudioHistory
{
    public const int DefaultMaxSteps = 20;
    /// <summary>About 256 MB of float samples.</summary>
    public const long DefaultMaxSamples = 64L * 1024 * 1024;

    private readonly List<AudioState> undo = new();
    private readonly List<AudioState> redo = new();

    public AudioHistory(AudioState initial, int maxSteps = DefaultMaxSteps, long maxSamples = DefaultMaxSamples)
    {
        Current = initial ?? throw new ArgumentNullException(nameof(initial));
        MaxSteps = Math.Max(1, maxSteps);
        MaxSamples = Math.Max(1, maxSamples);
        Initial = initial;
    }

    public int MaxSteps { get; }
    public long MaxSamples { get; }

    /// <summary>What the editor opened with.</summary>
    public AudioState Initial { get; }

    public AudioState Current { get; private set; }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;

    /// <summary>The sound is not what the editor opened with.</summary>
    public bool Changed => !ReferenceEquals(Current, Initial);

    /// <summary>A new version after an edit: it becomes current, and Redo is gone.</summary>
    public void Push(AudioState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        undo.Add(Current);
        Current = state;
        redo.Clear();
        Trim();
    }

    public bool Undo()
    {
        if (undo.Count == 0) return false;
        redo.Add(Current);
        Current = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (redo.Count == 0) return false;
        undo.Add(Current);
        Current = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        return true;
    }

    /// <summary>Samples held by every kept version, the current one included.</summary>
    public long TotalSamples
    {
        get
        {
            long t = Current.Samples.Length;
            foreach (var s in undo) t += s.Samples.Length;
            foreach (var s in redo) t += s.Samples.Length;
            return t;
        }
    }

    private void Trim()
    {
        while (undo.Count > MaxSteps) undo.RemoveAt(0);
        while (undo.Count > 1 && TotalSamples > MaxSamples) undo.RemoveAt(0);
    }
}
