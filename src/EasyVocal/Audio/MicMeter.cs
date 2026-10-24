using System;
using System.Threading;
using EasyVocal.Core.Engine;

namespace EasyVocal.Audio;

/// <summary>
/// Listens to one microphone only to show how loud it is (the setup window's level bar), whether or not the voice
/// engine is running. It plays nothing and never throws: when the device can't be opened <see cref="Working"/> is false.
/// </summary>
public sealed class MicMeter : IDisposable
{
    private IAudioInput? input;
    private float peak;

    /// <param name="devices">Where the microphone is opened (the controller's devices: the real ones, or the e2e test's).</param>
    /// <param name="deviceId">The endpoint id of the microphone to listen to.</param>
    public MicMeter(IAudioDevices devices, string deviceId)
    {
        try
        {
            input = devices.OpenMic(deviceId);
            input.Samples += OnSamples;
            input.Start();
        }
        catch (Exception)
        {
            input?.Dispose();
            input = null;
        }
    }

    public bool Working => input != null;

    /// <summary>The loudest sample (0..1) since the last call.</summary>
    public float TakeLevel() => Interlocked.Exchange(ref peak, 0f);

    /// <summary>Where a peak level sits on a bar: the last 60 dB mapped to 0..1 (quiet room = nearly empty).</summary>
    public static double BarFraction(double level) =>
        level <= 0.001 ? 0 : Math.Clamp((20 * Math.Log10(level) + 60) / 60, 0, 1);

    // Capture thread.
    private void OnSamples(float[] buffer, int count)
    {
        float max = 0f;
        for (int i = 0; i < count; i++)
        {
            float v = Math.Abs(buffer[i]);
            if (v > max) max = v;
        }
        float seen = Volatile.Read(ref peak);
        while (max > seen)
        {
            float before = Interlocked.CompareExchange(ref peak, max, seen);
            if (before == seen) break;
            seen = before;
        }
    }

    public void Dispose()
    {
        var i = input;
        input = null;
        if (i == null) return;
        i.Samples -= OnSamples;
        i.Dispose();
    }
}
