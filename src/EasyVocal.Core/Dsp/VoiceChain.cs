using System;
using System.Threading;
using EasyVocal.Core.Model;

namespace EasyVocal.Core.Dsp;

/// <summary>
/// The whole voice filter: shifter → robot → distortion → radio → whisper → echo, driven by seven sliders.
/// <para>
/// Threads: <see cref="Process"/> runs on the audio thread only; <see cref="Apply"/> and <see cref="Bypass"/> are
/// called from the UI thread and only publish a new immutable target, which <see cref="Process"/> picks up at the
/// start of its next call. Nothing the UI thread does ever touches an effect.
/// </para>
/// <para>
/// A neutral chain (or <see cref="Bypass"/>) is the clean input, untouched and with no latency. The shifter (and its
/// fixed delay) is only in the path while Pitch or Voice body is off the middle: Robot, Echo, Radio and the rest
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
/// </summary>
public sealed class VoiceChain
{
    private const double FadeSeconds = 0.020;
    private const double PrerollSeconds = 0.060;
    private const int SlewChunk = 32;
    /// <summary>A change above this fraction of a parameter's range is a "jump" and crossfades instead of sliding.</summary>
    private const double JumpFraction = 0.1;

    private static readonly double SemitoneRange = VoiceShifter.MaxSemitones - VoiceShifter.MinSemitones;
    private static readonly double FormantRange = VoiceShifter.MaxFormant - VoiceShifter.MinFormant;

    /// <summary>One immutable set of effect parameters (what the sliders mean), published by <see cref="Apply"/>.</summary>
    private sealed class Target
    {
        public readonly double Semitones, Formant, Robot, Echo, Radio, Distortion, Whisper;
        public readonly bool Neutral;
        /// <summary>Pitch or Voice body is used: the shifter (and its delay) must be in the path.</summary>
        public readonly bool Shifts;

        public Target(VoiceSliders sliders)
        {
            var s = sliders.Clamp();
            Semitones = s.Pitch * 12;
            Formant = s.Body >= 0 ? 1 + s.Body * 0.33 : 1 + s.Body * 0.25;
            Robot = s.Robot;
            Echo = s.Echo;
            Radio = s.Radio;
            Distortion = s.Distortion;
            Whisper = s.Whisper;
            Neutral = s.IsNeutral;
            Shifts = s.Pitch != 0 || s.Body != 0;
        }

        public bool FarFrom(Target o) =>
            Math.Abs(Semitones - o.Semitones) > JumpFraction * SemitoneRange ||
            Math.Abs(Formant - o.Formant) > JumpFraction * FormantRange ||
            Math.Abs(Robot - o.Robot) > JumpFraction ||
            Math.Abs(Echo - o.Echo) > JumpFraction ||
            Math.Abs(Radio - o.Radio) > JumpFraction ||
            Math.Abs(Distortion - o.Distortion) > JumpFraction ||
            Math.Abs(Whisper - o.Whisper) > JumpFraction;
    }

    /// <summary>A complete set of the six stages with the live (possibly still sliding) parameters.</summary>
    private sealed class Path
    {
        private readonly VoiceShifter shifter;
        private readonly RobotEffect robot;
        private readonly DistortionEffect distortion;
        private readonly RadioEffect radio;
        private readonly WhisperEffect whisper;
        private readonly EchoEffect echo;
        private readonly double slewFraction;
        private double semitones, formant, robotAmt, echoAmt, radioAmt, distortionAmt, whisperAmt;
        private bool sliding;
        private bool shifts;

        public Path(int sampleRate)
        {
            shifter = new VoiceShifter(sampleRate);
            robot = new RobotEffect(sampleRate);
            distortion = new DistortionEffect(sampleRate);
            radio = new RadioEffect(sampleRate);
            whisper = new WhisperEffect(sampleRate);
            echo = new EchoEffect(sampleRate);
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
            semitones = t.Semitones; formant = t.Formant; robotAmt = t.Robot; echoAmt = t.Echo;
            radioAmt = t.Radio; distortionAmt = t.Distortion; whisperAmt = t.Whisper;
            Commit();
            shifter.Reset(); robot.Reset(); distortion.Reset(); radio.Reset(); whisper.Reset(); echo.Reset();
        }

        public void SlideTo(Target t)
        {
            Goal = t;
            sliding = true;
        }

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
            if (shifts) shifter.Process(x);
            robot.Process(x);
            distortion.Process(x);
            radio.Process(x);
            whisper.Process(x);
            echo.Process(x);
        }

        private void Step()
        {
            var g = Goal!;
            semitones = Toward(semitones, g.Semitones, slewFraction * SemitoneRange);
            formant = Toward(formant, g.Formant, slewFraction * FormantRange);
            robotAmt = Toward(robotAmt, g.Robot, slewFraction);
            echoAmt = Toward(echoAmt, g.Echo, slewFraction);
            radioAmt = Toward(radioAmt, g.Radio, slewFraction);
            distortionAmt = Toward(distortionAmt, g.Distortion, slewFraction);
            whisperAmt = Toward(whisperAmt, g.Whisper, slewFraction);
            Commit();
            sliding = semitones != g.Semitones || formant != g.Formant || robotAmt != g.Robot || echoAmt != g.Echo ||
                      radioAmt != g.Radio || distortionAmt != g.Distortion || whisperAmt != g.Whisper;
        }

        private static double Toward(double v, double goal, double maxStep) =>
            Math.Abs(goal - v) <= maxStep ? goal : v + (goal > v ? maxStep : -maxStep);

        /// <summary>Pushes the live parameters into the stages. An effect that was at 0 is cleared first: it froze its state there.</summary>
        private void Commit()
        {
            shifter.PitchSemitones = semitones;
            shifter.FormantFactor = formant;
            if (robot.Amount <= 0 && robotAmt > 0) robot.Reset();
            robot.Amount = robotAmt;
            if (distortion.Amount <= 0 && distortionAmt > 0) distortion.Reset();
            distortion.Amount = distortionAmt;
            if (radio.Amount <= 0 && radioAmt > 0) radio.Reset();
            radio.Amount = radioAmt;
            if (whisper.Amount <= 0 && whisperAmt > 0) whisper.Reset();
            whisper.Amount = whisperAmt;
            if (echo.Amount <= 0 && echoAmt > 0) echo.Reset();
            echo.Amount = echoAmt;
        }
    }

    private readonly Path[] paths;
    private readonly int fadeLength;
    private readonly int prerollLength;
    private readonly float[] scratch = new float[1024];
    private readonly float[] history;     // the last input samples, to warm up a path that is about to take over
    private readonly int historyMask;
    private long historyCount;

    private Target pending = new Target(new VoiceSliders());
    private volatile bool bypass;

    // Audio-thread state.
    private Path? active;                 // null = the clean input
    private Path? outgoing;               // null while fading = the clean input fading out
    private bool fading;
    private int fadePos;

    public VoiceChain(int sampleRate)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        paths = new[] { new Path(sampleRate), new Path(sampleRate) };
        fadeLength = DspUtil.Samples(sampleRate, FadeSeconds);
        prerollLength = DspUtil.Samples(sampleRate, PrerollSeconds);
        int size = 1;
        while (size < prerollLength) size <<= 1;
        history = new float[size];
        historyMask = size - 1;
    }

    /// <summary>UI thread: set the sliders (takes effect on the next audio block).</summary>
    public void Apply(VoiceSliders sliders) => Volatile.Write(ref pending, new Target(sliders));

    /// <summary>UI thread: true = filter OFF, the clean input goes through.</summary>
    public bool Bypass
    {
        get => bypass;
        set => bypass = value;
    }

    /// <summary>
    /// Delay the chain adds once the latest <see cref="Apply"/>/<see cref="Bypass"/> has taken hold: the shifter's
    /// fixed delay when Pitch or Voice body is used, otherwise 0 (clean input, or effects without a shift).
    /// </summary>
    public int LatencySamples
    {
        get
        {
            var t = Volatile.Read(ref pending);
            return bypass || t.Neutral || !t.Shifts ? 0 : paths[0].ShifterLatency;
        }
    }

    /// <summary>Audio thread: processes one block of mono audio in place (any length).</summary>
    public void Process(Span<float> mono)
    {
        Decide();
        for (int i = 0; i < mono.Length; i += scratch.Length)
            ProcessChunk(mono.Slice(i, Math.Min(scratch.Length, mono.Length - i)));
    }

    private void Decide()
    {
        if (fading) return; // the newest target is looked at once the crossfade is over (20 ms)

        var t = Volatile.Read(ref pending);
        bool wanted = !bypass && !t.Neutral;
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

    private void ProcessChunk(Span<float> x)
    {
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
