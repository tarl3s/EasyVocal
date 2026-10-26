using System;
using System.Threading;
using System.Threading.Tasks;

namespace EasyVocal.Core.Dsp;

/// <summary>
/// Turns the voice into another one before the classic effects run (an AI voice: <c>EasyVocal.Ai</c>). Mono
/// 48 kHz, in place, on the audio thread.
/// <para>
/// <see cref="Process"/> must never block, allocate or wait for the conversion: it hands the input to its own worker
/// and gives back whatever converted audio is ready (and something sensible, e.g. the input, while it isn't).
/// <see cref="Reset"/> runs on the audio thread too. <see cref="Dispose"/> may come from any thread at any time,
/// also while <see cref="Process"/> runs: from then on Process leaves the audio untouched.
/// </para>
/// </summary>
public interface IVoiceConverter : IDisposable
{
    void Process(Span<float> mono);

    /// <summary>Audio thread: forget what was heard (the next block starts a new stream).</summary>
    void Reset();

    /// <summary>How far behind the input the converted voice comes out, in samples (any thread).</summary>
    int LatencySamples { get; }

    /// <summary>A second stream through the same voice with its own state (the sounds through the voice). UI thread.</summary>
    IVoiceConverter CreateFollower();
}

/// <summary>What the app asks for an AI voice; <c>EasyVocal.Ai</c> implements it (tests pass a stand-in).</summary>
public interface IVoiceConverterSource
{
    /// <summary>False when AI voices can't run here (no AI pack, no runtime); <see cref="UnavailableReason"/> says why.</summary>
    bool Available { get; }

    string? UnavailableReason { get; }

    /// <summary>Loads the voice (seconds: models are read and prepared) off the caller's thread.</summary>
    Task<IVoiceConverter> CreateAsync(string aiVoiceId, string? quality, CancellationToken ct);

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
