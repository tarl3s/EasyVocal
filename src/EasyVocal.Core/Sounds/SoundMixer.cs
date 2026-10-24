using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace EasyVocal.Core.Sounds;

/// <summary>
/// Plays soundboard sounds on top of the voice. The UI thread calls <see cref="Toggle"/>, <see cref="StopAll"/>,
/// <see cref="Set"/> and <see cref="Remove"/>; they only enqueue commands. The audio thread calls
/// <see cref="MixInto"/> every block, which drains the queue and mixes without locks or allocations.
/// </summary>
public sealed class SoundMixer
{
    public const int MaxVoices = 16;

    private enum CommandKind { Start, Stop, StopAll }

    private readonly struct Command
    {
        public Command(CommandKind kind, string id, float[]? samples, float volume, int token)
        {
            Kind = kind; Id = id; Samples = samples; Volume = volume; Token = token;
        }
        public readonly CommandKind Kind;
        public readonly string Id;
        public readonly float[]? Samples;
        public readonly float Volume;
        public readonly int Token;
    }

    private sealed class Voice
    {
        public bool Active;
        public string Id = "";
        public float[] Samples = Array.Empty<float>();
        public int Position;
        public float Volume;
        public int Token;
        public long Order;
    }

    // Sample arrays are never mutated: Set swaps the reference, voices keep the array they started with.
    private readonly ConcurrentDictionary<string, float[]> _sounds = new();
    // id -> token of the play that is (or is about to be) sounding. Written by the UI side; the audio thread only
    // removes the exact (id, token) pair of a play that ended by itself.
    private readonly ConcurrentDictionary<string, int> _playing = new();
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly Voice[] _voices = new Voice[MaxVoices]; // audio thread only
    private long _order;
    private int _token;

    public SoundMixer()
    {
        for (int i = 0; i < _voices.Length; i++) _voices[i] = new Voice();
    }

    /// <summary>Raised on the audio thread when a sound starts (true) or ends / is stopped (false).</summary>
    public event Action<string, bool>? PlayingChanged;

    /// <summary>Adds or replaces the samples for an id. A sound that is playing keeps playing the old samples.</summary>
    public void Set(string id, float[] samples)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0) Remove(id);
        else _sounds[id] = samples;
    }

    /// <summary>Forgets a sound and stops it if it is playing.</summary>
    public void Remove(string id)
    {
        _sounds.TryRemove(id, out _);
        if (_playing.TryRemove(id, out int token))
            _commands.Enqueue(new Command(CommandKind.Stop, id, null, 0, token));
    }

    /// <summary>Starts the sound, or stops it when it is already playing. True = it started.</summary>
    public bool Toggle(string id, float volume)
    {
        if (_playing.TryRemove(id, out int old))
        {
            _commands.Enqueue(new Command(CommandKind.Stop, id, null, 0, old));
            return false;
        }
        if (!_sounds.TryGetValue(id, out var samples)) return false;
        int token = Interlocked.Increment(ref _token);
        if (!_playing.TryAdd(id, token)) return true; // another thread started it a moment ago
        if (!(volume > 0)) volume = 0; // also catches NaN
        _commands.Enqueue(new Command(CommandKind.Start, id, samples, volume, token));
        return true;
    }

    public void StopAll()
    {
        _playing.Clear();
        _commands.Enqueue(new Command(CommandKind.StopAll, "", null, 0, 0));
    }

    /// <summary>True from the moment a sound is started until it ends or is stopped.</summary>
    public bool IsPlaying(string id) => _playing.ContainsKey(id);

    /// <summary>Audio thread: adds every playing sound to <paramref name="target"/>.</summary>
    public void MixInto(Span<float> target)
    {
        while (_commands.TryDequeue(out var cmd)) Apply(cmd);

        for (int v = 0; v < _voices.Length; v++)
        {
            var voice = _voices[v];
            if (!voice.Active) continue;
            var samples = voice.Samples;
            int pos = voice.Position;
            int n = Math.Min(target.Length, samples.Length - pos);
            float vol = voice.Volume;
            for (int i = 0; i < n; i++) target[i] += samples[pos + i] * vol;
            voice.Position = pos + n;
            if (voice.Position >= samples.Length) End(voice, naturalEnd: true);
        }
    }

    private void Apply(in Command cmd)
    {
        switch (cmd.Kind)
        {
            case CommandKind.Start:
                Start(cmd);
                break;
            case CommandKind.Stop:
                for (int i = 0; i < _voices.Length; i++)
                {
                    var voice = _voices[i];
                    if (voice.Active && voice.Token == cmd.Token && voice.Id == cmd.Id) End(voice, naturalEnd: false);
                }
                break;
            default:
                for (int i = 0; i < _voices.Length; i++)
                    if (_voices[i].Active) End(_voices[i], naturalEnd: false);
                break;
        }
    }

    private void Start(in Command cmd)
    {
        Voice? slot = null, oldest = null;
        for (int i = 0; i < _voices.Length; i++)
        {
            var voice = _voices[i];
            if (!voice.Active) { slot ??= voice; continue; }
            if (oldest == null || voice.Order < oldest.Order) oldest = voice;
        }
        if (slot == null)
        {
            End(oldest!, naturalEnd: true); // evicted: forget its play marker like an ended sound
            slot = oldest!;
        }
        slot.Active = true;
        slot.Id = cmd.Id;
        slot.Samples = cmd.Samples!;
        slot.Position = 0;
        slot.Volume = cmd.Volume;
        slot.Token = cmd.Token;
        slot.Order = ++_order;
        Raise(cmd.Id, true);
    }

    private void End(Voice voice, bool naturalEnd)
    {
        voice.Active = false;
        string id = voice.Id;
        if (naturalEnd) _playing.TryRemove(new KeyValuePair<string, int>(id, voice.Token));
        voice.Samples = Array.Empty<float>();
        Raise(id, false);
    }

    private void Raise(string id, bool playing)
    {
        try { PlayingChanged?.Invoke(id, playing); }
        catch { /* a faulty listener must never break the audio thread */ }
    }
}
