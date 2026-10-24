using System;
using System.Threading;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Sounds;

namespace EasyVocal.Core.Engine;

/// <summary>
/// Mic → voice filter → + soundboard → limiter → virtual cable (the call), and optionally the same mix to the
/// speakers so the user can hear it.
/// <para>
/// Per block of mic audio: <c>voice</c> = the mic through the <see cref="VoiceChain"/> (clean when
/// <see cref="FilterEnabled"/> is off, silent when <see cref="MicMuted"/>); <c>sounds</c> = the
/// <see cref="SoundMixer"/>; the call gets <c>Limiter(voice + sounds)</c>; the monitor gets
/// <c>Limiter((HearMe ? voice : 0) + (HearSounds ? sounds : 0))</c> and nothing at all when both are off.
/// When the call output holds more than 60 ms it is emptied, so latency never creeps up.
/// </para>
/// <para>
/// Threads: <see cref="Start"/>, <see cref="Stop"/>, <see cref="TryRecover"/> and <see cref="Dispose"/> belong to
/// the UI thread. The audio work runs on the mic's capture thread with reused buffers: no allocation, no lock.
/// A device that fails (an exception from the mic or from <see cref="IAudioOutput.Write"/>) only changes
/// <see cref="State"/>; it is disposed later on the UI thread (disposing a capture from its own thread would
/// deadlock). <see cref="StateChanged"/> may be raised on any thread.
/// </para>
/// </summary>
public sealed class AudioEngine : IDisposable
{
    /// <summary>Most audio the call output may hold before it is dropped (60 ms).</summary>
    public const int MaxCallBacklog = AudioFormat.SampleRate * 60 / 1000;

    private const int Block = AudioFormat.BlockSize;
    /// <summary>Mute / hear-me / hear-sounds switches fade over 5 ms instead of clicking.</summary>
    private const float RampStep = 1f / (AudioFormat.SampleRate * 5 / 1000);

    private readonly Func<IAudioInput> makeInput;
    private readonly Func<IAudioOutput> makeCallOutput;
    private readonly Func<IAudioOutput?> makeMonitor;
    private readonly VoiceChain chain;
    private readonly SoundMixer mixer;

    private readonly object gate = new();        // UI side only: start / stop / recover
    private readonly object stateLock = new();   // tiny: never held while creating or disposing devices
    private Session? session;                    // the devices in use (null when stopped)
    private EngineState state = EngineState.Stopped;

    private volatile bool micMuted, hearMe, hearSounds = true;
    private float inputLevel;
    private int processing;                      // 1 while a block is being processed (audio work never overlaps)

    // Audio-thread state (guarded by <processing>).
    private float voiceGain, meGain, soundsGain;
    private bool monitorWasSilent = true;

    /// <summary>The devices of one start, with the buffers and limiters their audio needs.</summary>
    private sealed class Session
    {
        public IAudioInput? Input;
        public IAudioOutput? Call;
        public IAudioOutput? Monitor;
        public Action<float[], int>? OnSamples;
        public Action<Exception?>? OnStopped;
        public volatile bool Failed;
        public volatile bool MonitorFailed;
        public readonly float[] VoiceBuf = new float[Block];
        public readonly float[] SoundsBuf = new float[Block];
        public readonly float[] CallBuf = new float[Block];
        public readonly float[] MonitorBuf = new float[Block];
        public readonly Limiter CallLimiter = new(AudioFormat.SampleRate);
        public readonly Limiter MonitorLimiter = new(AudioFormat.SampleRate);
    }

    public AudioEngine(Func<IAudioInput> makeInput, Func<IAudioOutput> makeCallOutput, Func<IAudioOutput?> makeMonitor,
        VoiceChain chain, SoundMixer mixer)
    {
        this.makeInput = makeInput ?? throw new ArgumentNullException(nameof(makeInput));
        this.makeCallOutput = makeCallOutput ?? throw new ArgumentNullException(nameof(makeCallOutput));
        this.makeMonitor = makeMonitor ?? throw new ArgumentNullException(nameof(makeMonitor));
        this.chain = chain ?? throw new ArgumentNullException(nameof(chain));
        this.mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
    }

    /// <summary>Raised when <see cref="State"/> changes, on whichever thread noticed it.</summary>
    public event Action<EngineState>? StateChanged;

    public EngineState State
    {
        get { lock (stateLock) return state; }
    }

    /// <summary>ON/OFF: off sends the clean voice (the chain crossfades to it, no click).</summary>
    public bool FilterEnabled
    {
        get => !chain.Bypass;
        set => chain.Bypass = !value;
    }

    /// <summary>Nothing of the voice reaches the call or the monitor; sounds still play.</summary>
    public bool MicMuted { get => micMuted; set => micMuted = value; }

    /// <summary>The user hears their own (changed) voice on the monitor.</summary>
    public bool HearMe { get => hearMe; set => hearMe = value; }

    /// <summary>The user hears the soundboard on the monitor (on by default).</summary>
    public bool HearSounds { get => hearSounds; set => hearSounds = value; }

    /// <summary>Peak of the raw mic in the last block, 0..1 (also while muted).</summary>
    public float InputLevel => Volatile.Read(ref inputLevel);

    /// <summary>Opens the devices and starts the audio. Does nothing while already running.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (session != null && State == EngineState.Running) return;
            TearDown();
            Build();
        }
    }

    /// <summary>Closes every device; <see cref="TryRecover"/> does nothing until the next <see cref="Start"/>.</summary>
    public void Stop()
    {
        lock (gate)
        {
            TearDown();
            Volatile.Write(ref inputLevel, 0f);
            SetState(EngineState.Stopped);
        }
    }

    /// <summary>
    /// After a device was lost (or the cable was missing), closes what is left and opens everything again.
    /// True when the engine is running afterwards. Never restarts an engine that was stopped on purpose.
    /// </summary>
    public bool TryRecover()
    {
        lock (gate)
        {
            var now = State;
            if (now == EngineState.Running) return true;
            if (now == EngineState.Stopped) return false;
            TearDown();
            Build();
            return State == EngineState.Running;
        }
    }

    public void Dispose() => Stop();

    // ---------- lifecycle (UI thread, under <gate>) ----------

    private void Build()
    {
        var s = new Session();
        try
        {
            s.Call = makeCallOutput();
        }
        catch (NoVirtualCableException)
        {
            SetState(EngineState.NoVirtualCable);
            return;
        }
        catch (Exception)
        {
            SetState(EngineState.OutputLost);
            return;
        }

        try { s.Monitor = makeMonitor(); }
        catch (Exception) { s.Monitor = null; } // no speakers: the call still works

        try
        {
            s.Input = makeInput();
        }
        catch (Exception)
        {
            Dispose(s);
            SetState(EngineState.MicLost);
            return;
        }

        // Fresh audio-thread state: nothing is running yet (the previous input was disposed, its thread joined).
        voiceGain = micMuted ? 0f : 1f;
        meGain = hearMe ? 1f : 0f;
        soundsGain = hearSounds ? 1f : 0f;
        monitorWasSilent = true;
        Volatile.Write(ref inputLevel, 0f);

        s.OnSamples = (buffer, count) => OnSamples(s, buffer, count);
        s.OnStopped = error => Lose(s, EngineState.MicLost);
        s.Input.Samples += s.OnSamples;
        s.Input.Stopped += s.OnStopped;
        lock (stateLock) session = s;

        try
        {
            s.Call.Start();
        }
        catch (Exception)
        {
            Lose(s, EngineState.OutputLost);
            return;
        }
        try { s.Monitor?.Start(); }
        catch (Exception) { s.MonitorFailed = true; }
        try
        {
            s.Input.Start();
        }
        catch (Exception)
        {
            Lose(s, EngineState.MicLost);
            return;
        }
        Action<EngineState>? raise = null;
        lock (stateLock)
        {
            if (s.Failed || state == EngineState.Running) return; // a device already failed: keep what Lose set
            state = EngineState.Running;
            raise = StateChanged;
        }
        Raise(raise, EngineState.Running);
    }

    private void TearDown()
    {
        Session? s;
        lock (stateLock)
        {
            s = session;
            session = null;
        }
        if (s != null) Dispose(s);
    }

    private static void Dispose(Session s)
    {
        s.Failed = true;
        if (s.Input != null)
        {
            if (s.OnSamples != null) s.Input.Samples -= s.OnSamples;
            if (s.OnStopped != null) s.Input.Stopped -= s.OnStopped;
            TryDispose(s.Input);
        }
        TryDispose(s.Call);
        TryDispose(s.Monitor);
    }

    private static void TryDispose(IDisposable? d)
    {
        try { d?.Dispose(); }
        catch (Exception) { /* a device that is already gone may fail to close: nothing left to do */ }
    }

    /// <summary>Any thread: the session's device failed. Only the current session can change the state.</summary>
    private void Lose(Session s, EngineState why)
    {
        Action<EngineState>? raise = null;
        lock (stateLock)
        {
            if (s.Failed || !ReferenceEquals(session, s)) return;
            s.Failed = true;
            if (state != why)
            {
                state = why;
                raise = StateChanged;
            }
        }
        Raise(raise, why);
    }

    private void SetState(EngineState next)
    {
        Action<EngineState>? raise = null;
        lock (stateLock)
        {
            if (state == next) return;
            state = next;
            raise = StateChanged;
        }
        Raise(raise, next);
    }

    private static void Raise(Action<EngineState>? handlers, EngineState value)
    {
        if (handlers == null) return;
        foreach (Action<EngineState> h in handlers.GetInvocationList())
        {
            try { h(value); }
            catch (Exception) { /* a faulty listener must never break the engine */ }
        }
    }

    // ---------- audio (capture thread) ----------

    private void OnSamples(Session s, float[] buffer, int count)
    {
        if (s.Failed || buffer == null) return;
        if (Interlocked.Exchange(ref processing, 1) == 1) return; // an old device's late block: skip it
        try
        {
            count = Math.Min(count, buffer.Length);
            float peak = 0;
            for (int at = 0; at < count && !s.Failed; at += Block)
                ProcessBlock(s, buffer.AsSpan(at, Math.Min(Block, count - at)), ref peak);
            Volatile.Write(ref inputLevel, peak > 1f ? 1f : peak);
        }
        catch (Exception)
        {
            // Not a device error (those are caught around Write): something unexpected. Never let it reach the
            // capture thread; report the input as lost so TryRecover starts everything afresh.
            Lose(s, EngineState.MicLost);
        }
        finally
        {
            Volatile.Write(ref processing, 0);
        }
    }

    private void ProcessBlock(Session s, ReadOnlySpan<float> mic, ref float peak)
    {
        int n = mic.Length;
        var voice = s.VoiceBuf.AsSpan(0, n);
        var sounds = s.SoundsBuf.AsSpan(0, n);
        var call = s.CallBuf.AsSpan(0, n);

        for (int i = 0; i < n; i++)
        {
            float v = mic[i];
            if (!float.IsFinite(v)) v = 0;
            float a = v < 0 ? -v : v;
            if (a > peak) peak = a;
            voice[i] = v;
        }

        // The chain always hears the mic (it stays warm while muted); muting only silences what it gives back.
        chain.Process(voice);
        Ramp(voice, ref voiceGain, micMuted ? 0f : 1f);

        sounds.Clear();
        mixer.MixInto(sounds);

        for (int i = 0; i < n; i++) call[i] = voice[i] + sounds[i];
        s.CallLimiter.Process(call);

        var callOut = s.Call!;
        try
        {
            if (callOut.BufferedSamples > MaxCallBacklog) callOut.DropBuffered();
            callOut.Write(call);
        }
        catch (Exception)
        {
            Lose(s, EngineState.OutputLost);
            return;
        }

        var monitor = s.Monitor;
        if (monitor == null || s.MonitorFailed) return;
        float meTarget = hearMe ? 1f : 0f, soundsTarget = hearSounds ? 1f : 0f;
        if (meTarget == 0f && soundsTarget == 0f && meGain == 0f && soundsGain == 0f)
        {
            monitorWasSilent = true;
            return;
        }
        if (monitorWasSilent)
        {
            s.MonitorLimiter.Reset(); // do not replay the few samples it held when it was last used
            monitorWasSilent = false;
        }

        // voice and sounds are not needed any more: scale them in place and sum.
        Ramp(voice, ref meGain, meTarget);
        Ramp(sounds, ref soundsGain, soundsTarget);
        var mon = s.MonitorBuf.AsSpan(0, n);
        for (int i = 0; i < n; i++) mon[i] = voice[i] + sounds[i];
        s.MonitorLimiter.Process(mon);
        try
        {
            if (monitor.BufferedSamples > MaxCallBacklog) monitor.DropBuffered();
            monitor.Write(mon);
        }
        catch (Exception)
        {
            s.MonitorFailed = true; // the speakers went away: the call carries on, the monitor stays off
        }
    }

    /// <summary>Multiplies by a gain that moves towards <paramref name="target"/> by <see cref="RampStep"/> per sample.</summary>
    private static void Ramp(Span<float> x, ref float gain, float target)
    {
        float g = gain;
        if (g == target)
        {
            if (g == 0f) x.Clear();
            else if (g != 1f) for (int i = 0; i < x.Length; i++) x[i] *= g;
            return;
        }
        for (int i = 0; i < x.Length; i++)
        {
            if (g < target) { g += RampStep; if (g > target) g = target; }
            else if (g > target) { g -= RampStep; if (g < target) g = target; }
            x[i] *= g;
        }
        gain = g;
    }
}
