using System.Threading;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;

namespace EasyVocal.Audio;

/// <summary>
/// Plays one sound on the headphones only (never into the call): the sound editor's "▶ Preview". It owns its own
/// output device, feeds it from a background thread and disposes it when the sound ends or <see cref="Dispose"/> is called.
/// </summary>
public sealed class SoundPreview : IDisposable
{
    /// <summary>Keep about 100 ms queued: short enough to stop quickly, long enough never to run dry.</summary>
    private const int QueueTarget = AudioFormat.SampleRate / 10;

    private readonly IAudioOutput output;
    private readonly float[] samples;
    private readonly float volume;
    private readonly Action ended;
    private readonly Thread thread;
    private volatile bool cancelled;

    /// <param name="ended">Called on the playback thread when the sound is over or stopped (post to the UI yourself).</param>
    public SoundPreview(IAudioOutput output, float[] samples, float volume, Action ended)
    {
        this.output = output;
        this.samples = samples;
        this.volume = float.IsFinite(volume) ? Math.Clamp(volume, 0f, 1f) : 0f;
        this.ended = ended;
        thread = new Thread(Run) { IsBackground = true, Name = "EasyVocal sound preview" };
        thread.Start();
    }

    private void Run()
    {
        try
        {
            output.Start();
            var chunk = new float[AudioFormat.BlockSize];
            int pos = 0;
            while (!cancelled && pos < samples.Length)
            {
                if (output.BufferedSamples >= QueueTarget)
                {
                    Thread.Sleep(5);
                    continue;
                }
                int n = Math.Min(chunk.Length, samples.Length - pos);
                for (int i = 0; i < n; i++) chunk[i] = Math.Clamp(samples[pos + i] * volume, -1f, 1f);
                output.Write(chunk.AsSpan(0, n));
                pos += n;
            }
            while (!cancelled && output.BufferedSamples > 0) Thread.Sleep(10); // let the tail finish
            if (!cancelled) Thread.Sleep(40);
        }
        catch (Exception)
        {
            // The headphones went away: the preview just ends.
        }
        finally
        {
            try { output.Dispose(); } catch (Exception) { /* already gone */ }
            try { ended(); } catch (Exception) { /* a faulty listener must not matter */ }
        }
    }

    /// <summary>Stops the sound and waits for the device to close.</summary>
    public void Dispose()
    {
        cancelled = true;
        if (Thread.CurrentThread != thread) thread.Join(1000);
    }
}
