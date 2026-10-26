using System;
using System.Threading;
using System.Threading.Tasks;

namespace EasyVocal.Core.Dsp;

/// <summary>
/// Turns the voice into another one before the classic effects run (an AI voice: <c>EasyVocal.Ai</c>). Mono
/// 48 kHz, in place, on the audio thread.
/// <para>
/// <see cref="Process"/> must never block, allocate or wait for the conversion: it hands the input to its own worker
/// and gives back whatever converted audio is ready (a stand-in voice while it isn't, never the input as it is).
/// <see cref="Reset"/> runs on the audio thread too. <see cref="Dispose"/> may come from any thread at any time,
/// also while <see cref="Process"/> runs: from then on Process gives silence or a stand-in voice, never the input as
/// it is (the chain disposes a converter only once it let go of it: <see cref="VoiceChain.Retire"/>).
/// </para>
/// </summary>
public interface IVoiceConverter : IDisposable
{
    void Process(Span<float> mono);

    /// <summary>Audio thread: forget what was heard (the next block starts a new stream).</summary>
    void Reset();

    /// <summary>How far behind the input the converted voice comes out, in samples (any thread).</summary>
    int LatencySamples { get; }

    /// <summary>
    /// It has given converted audio (any thread). A converter warming up in <see cref="VoiceChain.Warm"/> is swapped in
    /// only once it is ready, so nobody hears it start.
    /// </summary>
    bool Ready => true;

    /// <summary>Why it stopped converting (its worker failed), in plain words; null while it works (any thread).</summary>
    string? Error => null;
}


/// <summary>What the app asks for an AI voice; <c>EasyVocal.Ai</c> implements it (tests pass a stand-in).</summary>
public interface IVoiceConverterSource
{
    /// <summary>False when AI voices can't run here (no AI pack, no runtime); <see cref="UnavailableReason"/> says why.</summary>
    bool Available { get; }

    string? UnavailableReason { get; }

    /// <summary>Loads the voice (seconds: models are read and prepared) off the caller's thread.</summary>
    Task<IVoiceConverter> CreateAsync(string aiVoiceId, string? quality, CancellationToken ct);

    /// <summary>The same, on the processor only when <paramref name="cpuOnly"/> (the retry after a graphics card failed).</summary>
    Task<IVoiceConverter> CreateAsync(string aiVoiceId, string? quality, bool cpuOnly, CancellationToken ct) => CreateAsync(aiVoiceId, quality, ct);

    /// <summary>
    /// What plays while the voice loads and warms up when nothing else would hide the person's own voice (no voice, or
    /// a clean one before it): the mic shifted towards the AI voice, at once (no model). UI thread.
    /// </summary>
    IVoiceConverter StandIn(string aiVoiceId) => new StandInVoice(StandInVoice.DefaultSemitones, StandInVoice.DefaultFormant);

    /// <summary>Adds a voice card (<see cref="Model.VoiceKind.Ai"/>) for each installed AI voice that has none; true when it added one.</summary>
    bool SyncPresets(Model.AppSettings settings);
}

/// <summary>Where the active voice's AI part is.</summary>
public enum AiVoiceState
{
    /// <summary>A classic voice: no AI.</summary>
    Off,
    /// <summary>The AI voice is being loaded (the classic sliders play meanwhile).</summary>
    Loading,
    /// <summary>The AI voice plays.</summary>
    Ready,
    /// <summary>It couldn't be loaded (a damaged or unsupported model…).</summary>
    Failed,
    /// <summary>AI voices can't run here (no AI pack, no runtime).</summary>
    Unavailable,
}

/// <summary>
/// The stand-in voice: the mic shifted a few semitones (with the voice body following) so the person's own voice never
/// reaches the call while an AI voice is picked but not converting yet. Ready at once; no worker. Disposed = silence.
/// </summary>
public sealed class StandInVoice : IVoiceConverter
{
    /// <summary>A few semitones up with a lighter voice body: never the voice as it is.</summary>
    public const double DefaultSemitones = 4, DefaultFormant = 1.12;

    private readonly VoiceShifter shifter = new(AudioFormat.SampleRate);
    private volatile bool disposed;

    public StandInVoice(double semitones, double formant)
    {
        shifter.PitchSemitones = semitones;
        shifter.FormantFactor = formant;
    }

    public double Semitones => shifter.PitchSemitones;

    public int LatencySamples => shifter.LatencySamples;

    public void Process(Span<float> mono)
    {
        if (disposed) mono.Clear();
        else shifter.Process(mono);
    }

    public void Reset() => shifter.Reset();

    public void Dispose() => disposed = true;
}
