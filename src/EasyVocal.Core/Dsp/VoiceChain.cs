using System;
using System.Threading;
using EasyVocal.Core.Model;

namespace EasyVocal.Core.Dsp;

/// <summary>
/// The whole voice filter, driven by a <see cref="VoiceSliders"/>: shifter (pitch, body, wobble, monotone, plus an
/// optional layer an octave below) → growl → vocoder → robot → distortion → radio → whisper → echo → big room → tone.
/// <para>
/// Threads: <see cref="Process"/> runs on the audio thread only; <see cref="Apply"/> and <see cref="Bypass"/> are
/// called from the UI thread and only publish a new immutable target, which <see cref="Process"/> picks up at the
/// start of its next call. Nothing the UI thread does ever touches an effect. A <see cref="CreateFollower"/> copy
/// plays whatever its leader was given (the engine runs the soundboard through one).
/// </para>
/// <para>
/// A neutral chain (or <see cref="Bypass"/>) is the clean input, untouched and with no latency. The shifter (and its
/// fixed delay) is only in the path while <see cref="VoiceSliders.Shifts"/>: Robot, Echo, Radio and the rest
/// alone add no latency. Three things keep every change click-free:
/// </para>
/// <list type="bullet">
/// <item>Going to or from "clean", taking the shifter in or out, and jumping to a very different voice crossfades over 20 ms between the old output
/// and a second, freshly reset copy of the chain. That copy is first fed the last 60 ms of input so it has already
/// "heard" the voice and its output lines up with the old one (no 25 ms of silence).</item>
/// <item>Small changes (dragging a slider) move the live parameters towards the target in 1 ms steps, no faster
/// than the full range in 20 ms.</item>
/// <item>An effect whose amount comes back from 0 is reset first, so a tail it froze earlier is never replayed.</item>
/// </list>
/// The limiter is not part of the chain: the engine applies it after mixing in the sounds.
/// <para>
/// An AI voice adds a converter (<see cref="SetConverter"/>) in front of all of it: the mic goes through the converter
/// first and every effect above then plays on the converted voice. Taking a converter in or out (or OFF) crossfades
/// over 40 ms. A converter that was let go is disposed only once the audio
/// thread is done with it (<see cref="Retire"/>).
/// </para>
/// </summary>
public sealed class VoiceChain
{
    private const double FadeSeconds = 0.020;
    private const double PrerollSeconds = 0.060;
    private const int SlewChunk = 32;
    private const int ChunkMax = 1024;
    /// <summary>A change above this fraction of a parameter's range is a "jump" and crossfades instead of sliding.</summary>
    private const double JumpFraction = 0.1;
    private const double ConverterFadeSeconds = 0.040;

    // Every parameter the sliders turn into, by index, with its full range (for slides and "jumps").
    private const int Semis = 0, Formant = 1, Robot = 2, Echo = 3, Radio = 4, Distortion = 5, Whisper = 6, Growl = 7,
        Room = 8, Octave = 9, Bass = 10, Treble = 11, Wobble = 12, Monotone = 13, Vocoder = 14, Count = 15;
    private static readonly double[] Range =
    {
        VoiceShifter.MaxSemitones - VoiceShifter.MinSemitones, VoiceShifter.MaxFormant - VoiceShifter.MinFormant,
        1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 1, 1, 1,
    };

    /// <summary>The 0..1 effects after the shifter, in processing order, and the parameter each one plays.</summary>
    private static readonly int[] StageParam = { Growl, Vocoder, Robot, Distortion, Radio, Whisper, Echo, Room };

    /// <summary>One immutable set of effect parameters (what the sliders mean), published by <see cref="Apply"/>.</summary>
    private sealed class Target
    {
        public readonly double[] V = new double[Count];
        public readonly bool Neutral;
        /// <summary>The shifter (and its delay) must be in the path.</summary>
        public readonly bool Shifts;

        public Target(VoiceSliders sliders)
        {
            var s = sliders.Clamp();
            V[Semis] = s.Pitch * 12;
            V[Formant] = s.Body >= 0 ? 1 + s.Body * 0.33 : 1 + s.Body * 0.25;
            V[Robot] = s.Robot;
            V[Echo] = s.Echo;
            V[Radio] = s.Radio;
            V[Distortion] = s.Distortion;
            V[Whisper] = s.Whisper;
            V[Growl] = s.Growl;
            V[Room] = s.Room;
            V[Octave] = s.Octave;
            V[Bass] = s.Bass;
            V[Treble] = s.Treble;
            V[Wobble] = s.Wobble;
            V[Monotone] = s.Monotone;
            V[Vocoder] = s.Vocoder;
            Neutral = s.IsNeutral;
            Shifts = s.Shifts;
        }

        public bool FarFrom(Target o)
        {
            for (int i = 0; i < Count; i++)
                if (Math.Abs(V[i] - o.V[i]) > JumpFraction * Range[i]) return true;
            return false;
        }
    }

    /// <summary>A complete set of the stages with the live (possibly still sliding) parameters.</summary>
    private sealed class Path
    {
        private readonly VoiceShifter shifter;
        private readonly VoiceShifter layer;          // the octave-down voice mixed in by Octave
        private readonly float[] layerBuf = new float[ChunkMax];
        private readonly IAmountEffect[] stages;
        private readonly ToneEffect tone;
        private readonly double slewFraction;
        private readonly double[] live = new double[Count];
        private bool sliding;
        private bool shifts;
        private bool layerOn;

        public Path(int sampleRate)
        {
            shifter = new VoiceShifter(sampleRate);
            layer = new VoiceShifter(sampleRate, VoiceShifter.MinSemitones - 12) { VoicedOnly = true };
            stages = new IAmountEffect[]
            {
                new GrowlEffect(sampleRate), new VocoderEffect(sampleRate), new RobotEffect(sampleRate),
                new DistortionEffect(sampleRate), new RadioEffect(sampleRate), new WhisperEffect(sampleRate),
                new EchoEffect(sampleRate), new ReverbEffect(sampleRate),
            };
            tone = new ToneEffect(sampleRate);
            slewFraction = SlewChunk / (FadeSeconds * sampleRate);
        }

        /// <summary>The shifter's delay; the path only carries it while <see cref="Target.Shifts"/>.</summary>
        public int ShifterLatency => shifter.LatencySamples;
        public Target? Goal { get; private set; }

        /// <summary>The shifter is part of this path (decided by <see cref="Begin"/>; a slide never changes it).</summary>
        public bool Shifts => shifts;

        /// <summary>Starts from nothing: parameters set at once, every delay line and filter cleared.</summary>
        public void Begin(Target t)
        {
            Goal = t;
            sliding = false;
            shifts = t.Shifts;
            Array.Copy(t.V, live, Count);
            Commit();
            shifter.Reset();
            layer.Reset();
            for (int s = 0; s < stages.Length; s++) stages[s].Reset();
            tone.Reset();
        }

        public void SlideTo(Target t)
        {
            Goal = t;
            sliding = true;
        }

        /// <param name="x">At most <see cref="ChunkMax"/> samples.</param>
        public void Process(Span<float> x)
        {
            if (!sliding)
            {
                Run(x);
                return;
            }
            for (int i = 0; i < x.Length; i += SlewChunk)
            {
                Step();
                Run(x.Slice(i, Math.Min(SlewChunk, x.Length - i)));
            }
        }

        private void Run(Span<float> x)
        {
            if (shifts)
            {
                if (layerOn)
                {
                    // Both shifters have the same fixed delay, so the layer lines up with the voice.
                    var low = layerBuf.AsSpan(0, x.Length);
                    x.CopyTo(low);
                    layer.Process(low);
                    shifter.Process(x);
                    double oct = live[Octave];
                    float keep = (float)(1 - 0.3 * oct), add = (float)(0.9 * oct);
                    for (int k = 0; k < x.Length; k++) x[k] = x[k] * keep + low[k] * add;
                }
                else shifter.Process(x);
            }
            for (int s = 0; s < stages.Length; s++) stages[s].Process(x);
            tone.Process(x);
        }

        private void Step()
        {
            var g = Goal!.V;
            bool still = false;
            for (int i = 0; i < Count; i++)
            {
                live[i] = Toward(live[i], g[i], slewFraction * Range[i]);
                if (live[i] != g[i]) still = true;
            }
            Commit();
            sliding = still;
        }

        private static double Toward(double v, double goal, double maxStep) =>
            Math.Abs(goal - v) <= maxStep ? goal : v + (goal > v ? maxStep : -maxStep);

        /// <summary>Pushes the live parameters into the stages. A stage that was off is cleared first: it froze its state there.</summary>
        private void Commit()
        {
            shifter.PitchSemitones = live[Semis];
            shifter.FormantFactor = live[Formant];
            shifter.Wobble = live[Wobble];
            shifter.Monotone = live[Monotone];

            bool layerWanted = live[Octave] > 0;
            if (layerWanted && !layerOn) layer.Reset();
            layerOn = layerWanted;
            layer.PitchSemitones = live[Semis] - 12;
            layer.FormantFactor = live[Formant];
            layer.Wobble = live[Wobble];
            layer.Monotone = live[Monotone];

            for (int s = 0; s < stages.Length; s++)
            {
                var fx = stages[s];
                double v = live[StageParam[s]];
                if (fx.Amount <= 0 && v > 0) fx.Reset();
                fx.Amount = v;
            }

            bool toneWanted = live[Bass] != 0 || live[Treble] != 0;
            if (toneWanted && !tone.Active) tone.Reset();
            tone.Bass = live[Bass];
            tone.Treble = live[Treble];
        }
    }

    private readonly int sampleRate;
    private readonly VoiceChain? leader;  // a follower plays its leader's target and ON/OFF
    private readonly Path[] paths;
    private readonly int fadeLength;
    private readonly int prerollLength;
    private readonly float[] scratch = new float[ChunkMax];
    private readonly float[] history;     // the last input samples, to warm up a path that is about to take over
    private readonly int historyMask;
    private long historyCount;

    private Target pending = new Target(new VoiceSliders());
    private volatile bool bypass;

    // The converter (AI voice) in front of the effects. The UI publishes it; the audio thread crossfades to it.
    private IVoiceConverter? converterPending;
    private IVoiceConverter? converterWarm;      // UI → audio: fed the input but not heard, until it is swapped in
    private IVoiceConverter? fedLast;            // audio thread: the warm converter fed in the last block
    private readonly float[] warmScratch = new float[ChunkMax];
    private readonly float[] converterScratch = new float[ChunkMax];
    private readonly int converterFadeLength;
    private IVoiceConverter? converter;          // audio thread: in use (null = none)
    private IVoiceConverter? converterOutgoing;  // audio thread: fading out
    private bool converterFading;
    private int converterFadePos;

    // Audio-thread state.
    private Path? active;                 // null = the clean input
    private Path? outgoing;               // null while fading = the clean input fading out
    private bool fading;
    private int fadePos;

    public VoiceChain(int sampleRate) : this(sampleRate, null) { }

    private VoiceChain(int sampleRate, VoiceChain? leader)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.sampleRate = sampleRate;
        this.leader = leader;
        paths = new[] { new Path(sampleRate), new Path(sampleRate) };
        fadeLength = DspUtil.Samples(sampleRate, FadeSeconds);
        converterFadeLength = DspUtil.Samples(sampleRate, ConverterFadeSeconds);
        prerollLength = DspUtil.Samples(sampleRate, PrerollSeconds);
        int size = 1;
        while (size < prerollLength) size <<= 1;
        history = new float[size];
        historyMask = size - 1;
    }

    /// <summary>
    /// A second chain with its own state that always plays this chain's sliders and ON/OFF (its own
    /// <see cref="Apply"/> and <see cref="Bypass"/> are ignored): another signal through the same voice. It never gets a
    /// converter (the engine mixes the sounds into an AI voice's own stream instead of running a second one).
    /// </summary>
    public VoiceChain CreateFollower() => new VoiceChain(sampleRate, leader ?? this);

    /// <summary>
    /// UI thread: puts a converter (an AI voice) in front of the effects, or takes it out (null); crossfades on the
    /// audio thread. The caller keeps owning <paramref name="next"/> and hands the one it replaces to
    /// <see cref="Retire"/>. Ignored on a follower.
    /// </summary>
    public void SetConverter(IVoiceConverter? next)
    {
        if (leader != null) return;
        if (ReferenceEquals(Volatile.Read(ref converterPending), next)) return;
        bool warmed = next != null && ReferenceEquals(Volatile.Read(ref converterWarm), next);
        Volatile.Write(ref converterPending, next); // before the warm slot is cleared: the audio thread never feeds it twice
        if (warmed) Volatile.Write(ref converterWarm, null);
    }

    /// <summary>
    /// UI thread: feeds <paramref name="next"/> the same input as the voice without it being heard, so it is already
    /// converting when <see cref="SetConverter"/> swaps it in (an AI voice starts its stream while the previous voice
    /// keeps playing). Null stops warming. The caller keeps owning it, as with SetConverter. Ignored on a follower.
    /// </summary>
    public void Warm(IVoiceConverter? next)
    {
        if (leader != null) return;
        Volatile.Write(ref converterWarm, next);
    }

    // Converters waiting for the audio thread to let go of them (see Retire).
    private readonly System.Collections.Generic.List<(IVoiceConverter C, int FreeChecks)> retiring = new();
    private Timer? retireTimer;
    private bool retireNow;
    private const int RetireCheckMs = 50;

    /// <summary>
    /// Disposes <paramref name="c"/> once this chain no longer uses it: not set, not warming, and the audio thread
    /// has finished its crossfade away from it (two checks 50 ms apart find it free). Until then it keeps playing what
    /// it plays, so the chain never holds a disposed converter. Any thread; never the audio thread's work.
    /// </summary>
    public void Retire(IVoiceConverter? c)
    {
        if (c == null) return;
        lock (retiring)
        {
            if (!retireNow)
            {
                foreach (var r in retiring) if (ReferenceEquals(r.C, c)) return;
                retiring.Add((c, 0));
                retireTimer ??= new Timer(_ => RetireCheck(), null, RetireCheckMs, RetireCheckMs);
                return;
            }
        }
        DisposeQuietly(c); // the audio has stopped for good
    }

    /// <summary>
    /// The audio has stopped for good (the engine was disposed): every converter waiting in <see cref="Retire"/> is
    /// disposed now, and so is any retired later.
    /// </summary>
    public void RetireAllNow()
    {
        IVoiceConverter[] all;
        lock (retiring)
        {
            retireNow = true;
            all = new IVoiceConverter[retiring.Count];
            for (int i = 0; i < all.Length; i++) all[i] = retiring[i].C;
            retiring.Clear();
            retireTimer?.Dispose();
            retireTimer = null;
        }
        foreach (var c in all) DisposeQuietly(c);
    }

    /// <summary>The chain still uses <paramref name="c"/>: set, warming, playing or fading out (any thread).</summary>
    public bool Holds(IVoiceConverter c) =>
        ReferenceEquals(Volatile.Read(ref converterPending), c) || ReferenceEquals(Volatile.Read(ref converterWarm), c)
        || ReferenceEquals(Volatile.Read(ref converter), c) || ReferenceEquals(Volatile.Read(ref converterOutgoing), c)
        || ReferenceEquals(Volatile.Read(ref fedLast), c);

    private void RetireCheck()
    {
        var free = new System.Collections.Generic.List<IVoiceConverter>();
        lock (retiring)
        {
            for (int i = retiring.Count - 1; i >= 0; i--)
            {
                var (c, checks) = retiring[i];
                checks = Holds(c) ? 0 : checks + 1;
                if (checks >= 2)
                {
                    free.Add(c);
                    retiring.RemoveAt(i);
                }
                else retiring[i] = (c, checks);
            }
            if (retiring.Count == 0)
            {
                retireTimer?.Dispose();
                retireTimer = null;
            }
        }
        foreach (var c in free) DisposeQuietly(c);
    }

    private static void DisposeQuietly(IVoiceConverter c)
    {
        try { c.Dispose(); }
        catch (Exception) { /* a converter that fails to close must not stop the others */ }
    }

    /// <summary>The converter being warmed up (see <see cref="Warm"/>); null = none.</summary>
    public IVoiceConverter? Warming => Volatile.Read(ref converterWarm);

    /// <summary>The converter the chain plays (or fades to); null = none.</summary>
    public IVoiceConverter? Converter => Volatile.Read(ref converterPending);

    private VoiceChain Owner => leader ?? this;

    /// <summary>UI thread: set the sliders (takes effect on the next audio block).</summary>
    public void Apply(VoiceSliders sliders) => Volatile.Write(ref pending, new Target(sliders));

    /// <summary>UI thread: true = filter OFF, the clean input goes through.</summary>
    public bool Bypass
    {
        get => Owner.bypass;
        set => bypass = value;
    }

    /// <summary>
    /// Delay the chain adds once the latest <see cref="Apply"/>/<see cref="Bypass"/> has taken hold: the shifter's
    /// fixed delay when it is needed, otherwise 0 (clean input, or effects without a shift).
    /// </summary>
    public int LatencySamples
    {
        get
        {
            var t = Volatile.Read(ref Owner.pending);
            int effects = Owner.bypass || t.Neutral || !t.Shifts ? 0 : paths[0].ShifterLatency;
            var c = Owner.bypass ? null : Volatile.Read(ref converterPending);
            return effects + (c?.LatencySamples ?? 0);
        }
    }

    /// <summary>
    /// Audio thread: forgets everything the chain heard (every tail, delay line and crossfade); the next
    /// <see cref="Process"/> starts from silence with the current voice, at once.
    /// </summary>
    public void Reset()
    {
        converterOutgoing = null;
        converterFading = false;
        converter = Owner.bypass ? null : Volatile.Read(ref converterPending);
        converter?.Reset();
        Volatile.Read(ref converterWarm)?.Reset();
        Array.Clear(history);
        historyCount = 0;
        outgoing = null;
        fading = false;
        active = null;
        var owner = Owner;
        var t = Volatile.Read(ref owner.pending);
        if (owner.bypass || t.Neutral) return;
        active = paths[0];
        active.Begin(t);
    }

    /// <summary>Audio thread: processes one block of mono audio in place (any length).</summary>
    public void Process(Span<float> mono)
    {
        Decide();
        DecideConverter();
        var warm = Volatile.Read(ref converterWarm);
        if (ReferenceEquals(warm, converter) || ReferenceEquals(warm, converterOutgoing)) warm = null; // heard already: fed below
        fedLast = warm;
        for (int i = 0; i < mono.Length; i += scratch.Length)
        {
            var chunk = mono.Slice(i, Math.Min(scratch.Length, mono.Length - i));
            if (warm != null)
            {
                var w = warmScratch.AsSpan(0, chunk.Length);
                chunk.CopyTo(w);
                warm.Process(w); // the input the voice gets; its output isn't used yet
            }
            ProcessChunk(chunk);
        }
    }

    private void Decide()
    {
        if (fading) return; // the newest target is looked at once the crossfade is over (20 ms)

        var owner = Owner;
        var t = Volatile.Read(ref owner.pending);
        bool wanted = !owner.bypass && !t.Neutral;
        if (active == null)
        {
            if (wanted) StartFade(t);
        }
        else if (!wanted) StartFade(null);
        else if (!ReferenceEquals(t, active.Goal))
        {
            // Taking the shifter in or out changes the delay: that needs a crossfade, a slide would jump.
            if (t.FarFrom(active.Goal!) || t.Shifts != active.Shifts) StartFade(t);
            else active.SlideTo(t);
        }
    }

    /// <summary>Hands the output over to a fresh path with <paramref name="target"/> (null = the clean input).</summary>
    private void StartFade(Target? target)
    {
        Path? incoming = null;
        if (target != null)
        {
            incoming = ReferenceEquals(active, paths[0]) ? paths[1] : paths[0];
            incoming.Begin(target);
            Preroll(incoming);
        }
        outgoing = active;
        active = incoming;
        fading = true;
        fadePos = 0;
    }

    /// <summary>Runs the recent input through a fresh path (output thrown away) so it is "warm" when the fade starts.</summary>
    private void Preroll(Path p)
    {
        long avail = Math.Min(historyCount, prerollLength);
        long pos = historyCount - avail;
        while (avail > 0)
        {
            int n = (int)Math.Min(avail, scratch.Length);
            for (int k = 0; k < n; k++) scratch[k] = history[(int)((pos + k) & historyMask)];
            p.Process(scratch.AsSpan(0, n));
            pos += n;
            avail -= n;
        }
    }

    /// <summary>Audio thread: starts a crossfade when the converter to play changed (another one, none, or OFF).</summary>
    private void DecideConverter()
    {
        if (converterFading) return;
        var want = Owner.bypass ? null : Volatile.Read(ref converterPending);
        if (ReferenceEquals(want, converter)) return;
        // One that wasn't being fed until now (after OFF, or after none) starts a fresh stream: never audio it held from before.
        if (want != null && !ReferenceEquals(want, fedLast)) want.Reset();
        converterOutgoing = converter;
        converter = want;
        converterFading = true;
        converterFadePos = 0;
    }

    /// <summary>Audio thread: the converter's voice in place of the input (crossfading while it changes).</summary>
    private void Convert(Span<float> x)
    {
        if (!converterFading)
        {
            converter?.Process(x);
            return;
        }
        var old = converterScratch.AsSpan(0, x.Length);
        x.CopyTo(old);
        converterOutgoing?.Process(old);
        converter?.Process(x);
        int len = Math.Min(x.Length, converterFadeLength - converterFadePos);
        for (int k = 0; k < len; k++)
        {
            float g = (converterFadePos + k + 1) / (float)converterFadeLength;
            x[k] = old[k] * (1f - g) + x[k] * g;
        }
        converterFadePos += len;
        if (converterFadePos >= converterFadeLength)
        {
            converterFading = false;
            converterOutgoing = null;
        }
    }

    private void ProcessChunk(Span<float> x)
    {
        Convert(x);
        for (int k = 0; k < x.Length; k++) history[(int)((historyCount + k) & historyMask)] = x[k];
        historyCount += x.Length;

        if (!fading)
        {
            active?.Process(x); // no path: the clean input, untouched
            return;
        }

        var old = scratch.AsSpan(0, x.Length);
        x.CopyTo(old);
        outgoing?.Process(old);
        active?.Process(x);

        int len = Math.Min(x.Length, fadeLength - fadePos);
        for (int k = 0; k < len; k++)
        {
            float g = (fadePos + k + 1) / (float)fadeLength;
            x[k] = old[k] * (1f - g) + x[k] * g;
        }
        fadePos += len;
        if (fadePos >= fadeLength)
        {
            fading = false;
            outgoing = null;
        }
    }
}
