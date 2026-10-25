using System;

namespace EasyVocal.Core.Dsp;

/// <summary>
/// A mono in-place effect. Runs on the audio thread: implementations must not allocate,
/// lock or use LINQ inside <see cref="Process"/>.
/// </summary>
public interface IAudioEffect
{
    void Process(Span<float> mono);

    /// <summary>Clears every delay line and filter state (silence in, silence out).</summary>
    void Reset();
}

/// <summary>An effect with one 0..1 strength; 0 leaves the buffer bit-exact.</summary>
public interface IAmountEffect : IAudioEffect
{
    double Amount { get; set; }
}
