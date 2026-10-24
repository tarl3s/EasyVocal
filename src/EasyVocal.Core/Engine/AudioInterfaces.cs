using System;

namespace EasyVocal.Core.Engine;

/// <summary>An audio endpoint as Windows lists it. <see cref="Id"/> is the stable endpoint id saved in settings.</summary>
public sealed record DeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>A microphone delivering mono float samples at <see cref="Dsp.AudioFormat.SampleRate"/>.</summary>
public interface IAudioInput : IDisposable
{
    /// <summary>
    /// Raised on the capture thread with (buffer, count): <c>count</c> mono 48 kHz samples at the start of a buffer
    /// the input reuses (read it before returning). The count varies from call to call.
    /// </summary>
    event Action<float[], int>? Samples;

    /// <summary>Raised when capture ends; the exception (null when none) tells why, e.g. the device was unplugged.</summary>
    event Action<Exception?>? Stopped;

    void Start();
}

/// <summary>A playback device fed mono float samples at <see cref="Dsp.AudioFormat.SampleRate"/>.</summary>
public interface IAudioOutput : IDisposable
{
    void Start();

    /// <summary>Queues samples for playback (called from the capture thread). Throws when the device is gone.</summary>
    void Write(ReadOnlySpan<float> mono);

    /// <summary>Samples queued and not yet played.</summary>
    int BufferedSamples { get; }

    /// <summary>
    /// Throws away the oldest queued samples so only <paramref name="keepSamples"/> remain (keeps the latency from
    /// creeping up when the mic's clock runs faster than the device's), with a short crossfade at the cut.
    /// </summary>
    void TrimTo(int keepSamples);
}

/// <summary>Thrown by the call-output factory when the virtual cable (VB-CABLE) is not installed.</summary>
public sealed class NoVirtualCableException : Exception
{
    public NoVirtualCableException() : base("The virtual microphone (VB-CABLE) is not installed.") { }

    public NoVirtualCableException(string message) : base(message) { }

    public NoVirtualCableException(string message, Exception inner) : base(message, inner) { }
}

public enum EngineState
{
    Stopped,
    Running,
    MicLost,
    OutputLost,
    NoVirtualCable,
}
