using System;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EasyVocal.Core.Sounds;

/// <summary>
/// The sound editor's operations: pure functions on mono samples at <see cref="AudioFormat.SampleRate"/>. Nothing is
/// changed in place; every call returns a new array (the editor keeps the old one for Undo). A "range" is a start
/// sample and a count; ranges are clamped to the sound, so a bad one never throws.
/// </summary>
public static class AudioEdit
{
    private const int Sr = AudioFormat.SampleRate;

    /// <summary>The speeds the editor offers (1 = as it is, also to undo a speed change; 2 = twice as fast, half as long).</summary>
    public static readonly double[] Speeds = { 0.5, 0.75, 1, 1.25, 1.5, 2 };

    /// <summary>The pitch steps the editor offers, in semitones.</summary>
    public static readonly int[] PitchSteps = { 1, 2, 3, 5, 7, 12 };

    /// <summary>"Louder" is +3 dB; "Quieter" is the inverse.</summary>
    public const double LouderFactor = 1.4125375446;

    /// <summary>-1 dBFS: as loud as it gets without touching the ceiling.</summary>
    public const double NormalizePeak = 0.8912509381;

    /// <summary>A fade with nothing selected covers this much of the start or end.</summary>
    public const double DefaultFadeSeconds = 0.5;

    /// <summary>Quieter than this (about -40 dBFS) counts as silence for "Remove silence".</summary>
    public const float SilenceThreshold = 0.01f;

    /// <summary>"Remove silence" keeps this much before the first and after the last sound, so nothing starts abruptly.</summary>
    public const double SilenceMarginSeconds = 0.01;

    public enum EchoStrength { Light, Strong }

    // ---------- ranges ----------

    /// <summary>The range clamped to a sound of <paramref name="total"/> samples (a negative count = nothing).</summary>
    public static (int Start, int Count) Clamp(int total, int start, int count)
    {
        int s = Math.Clamp(start, 0, total);
        long end = Math.Clamp((long)start + Math.Max(0, count), s, total);
        return (s, (int)(end - s));
    }

    public static float[] Slice(float[] x, int start, int count)
    {
        (start, count) = Clamp(x.Length, start, count);
        var o = new float[count];
        Array.Copy(x, start, o, 0, count);
        return o;
    }

    /// <summary>The sound with the range replaced by <paramref name="part"/> (which may be longer or shorter).</summary>
    public static float[] Splice(float[] x, int start, int count, float[] part)
    {
        (start, count) = Clamp(x.Length, start, count);
        var o = new float[x.Length - count + part.Length];
        Array.Copy(x, 0, o, 0, start);
        Array.Copy(part, 0, o, start, part.Length);
        Array.Copy(x, start + count, o, start + part.Length, x.Length - start - count);
        return o;
    }

    /// <summary>Runs <paramref name="op"/> on the range only and puts the result back in its place.</summary>
    public static float[] OnRange(float[] x, int start, int count, Func<float[], float[]> op)
    {
        (start, count) = Clamp(x.Length, start, count);
        return Splice(x, start, count, op(Slice(x, start, count)));
    }

    /// <summary>Deletes the range.</summary>
    public static float[] Cut(float[] x, int start, int count) => Splice(x, start, count, Array.Empty<float>());

    /// <summary>Keeps only the range.</summary>
    public static float[] Trim(float[] x, int start, int count) => Slice(x, start, count);

    /// <summary>The part before <paramref name="at"/> and the part from it on.</summary>
    public static (float[] First, float[] Second) Split(float[] x, int at)
    {
        at = Math.Clamp(at, 0, x.Length);
        return (Slice(x, 0, at), Slice(x, at, x.Length - at));
    }

    // ---------- speed and pitch ----------

    /// <summary>
    /// Faster (<paramref name="speed"/> &gt; 1, shorter) or slower (&lt; 1, longer). <paramref name="keepPitch"/>:
    /// a time-stretch (WSOLA) that keeps the voice's pitch; otherwise a plain resample (a tape played faster:
    /// higher too).
    /// </summary>
    public static float[] ChangeSpeed(float[] x, double speed, bool keepPitch)
    {
        if (!(speed > 0) || double.IsInfinity(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        if (speed == 1) return (float[])x.Clone();
        return keepPitch ? TimeStretch(x, speed) : Resample(x, speed);
    }

    /// <summary>Higher (positive) or lower (negative) by <paramref name="semitones"/>, same length.</summary>
    public static float[] ShiftPitch(float[] x, double semitones)
    {
        if (semitones == 0 || x.Length == 0) return (float[])x.Clone();
        double ratio = Math.Pow(2, semitones / 12.0);
        // Stretch to ratio × as long (pitch kept), then play that ratio × faster: the length comes back, the pitch moves.
        var longer = TimeStretch(x, 1 / ratio);
        return FitLength(Resample(longer, ratio), x.Length);
    }

    /// <summary>Plays the sound <paramref name="speed"/> times as fast: length / speed, pitch × speed.</summary>
    public static float[] Resample(float[] x, double speed)
    {
        int target = (int)Math.Round(x.Length / speed);
        if (x.Length == 0 || target == 0) return new float[target];
        // Pretend the samples were recorded at speed × the rate and convert them back to the rate.
        int fromRate = (int)Math.Round(Sr * speed);
        var source = new ArraySource(x, fromRate, padding: 4096);
        var resampler = new WdlResamplingSampleProvider(source, Sr);
        var o = new float[target];
        var chunk = new float[8192];
        int total = 0;
        while (total < target)
        {
            int n = resampler.Read(chunk, 0, chunk.Length);
            if (n <= 0) break;
            int take = Math.Min(n, target - total);
            Array.Copy(chunk, 0, o, total, take);
            total += take;
        }
        return Finite(o);
    }

    /// <summary>
    /// WSOLA time-stretch: 32 ms Hann frames overlap-added at a fixed 16 ms output hop; each frame is read from
    /// near where the speed says, at the offset (±8 ms) that best continues the previous frame, so the waveform
    /// stays continuous and the pitch is kept. Length = input / speed.
    /// </summary>
    public static float[] TimeStretch(float[] x, double speed)
    {
        if (!(speed > 0)) throw new ArgumentOutOfRangeException(nameof(speed));
        int outLen = (int)Math.Round(x.Length / speed);
        const int N = 1536, Hop = N / 2, Tol = 384;
        if (x.Length < 2 * N)
        {
            // Shorter than two frames: stretch it with silence after it, then keep its own share of the result.
            if (x.Length == 0) return Array.Empty<float>();
            var padded = new float[2 * N];
            Array.Copy(x, padded, x.Length);
            return FitLength(TimeStretch(padded, speed), outLen);
        }

        var win = new double[N];
        for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
        var y = new double[outLen + N];
        var wsum = new double[outLen + N];
        int prev = 0;
        for (int k = 0; ; k++)
        {
            int outPos = k * Hop;
            if (outPos >= outLen) break;
            int pos;
            if (k == 0) pos = 0;
            else
            {
                int nominal = (int)Math.Round(outPos * speed);
                pos = BestMatch(x, prev + Hop, nominal - Tol, nominal + Tol, Hop);
            }
            for (int i = 0; i < N; i++)
            {
                int idx = pos + i;
                float s = idx < x.Length ? x[idx] : 0f;
                y[outPos + i] += s * win[i];
                wsum[outPos + i] += win[i];
            }
            prev = pos;
        }
        var o = new float[outLen];
        for (int i = 0; i < outLen; i++) o[i] = wsum[i] > 1e-9 ? (float)(y[i] / wsum[i]) : 0f;
        return Finite(o);
    }

    /// <summary>The start in [lo, hi] whose next <paramref name="len"/> samples look most like the ones at <paramref name="natural"/>.</summary>
    private static int BestMatch(float[] x, int natural, int lo, int hi, int len)
    {
        int last = x.Length - len;
        lo = Math.Clamp(lo, 0, Math.Max(0, last));
        hi = Math.Clamp(hi, lo, Math.Max(0, last));
        if (natural + len > x.Length || natural < 0) return (lo + hi) / 2;
        int best = (lo + hi) / 2;
        double bestScore = double.NegativeInfinity;
        // Coarse search, then a fine one around the winner.
        for (int p = lo; p <= hi; p += 4)
        {
            double score = Similarity(x, natural, p, len, 2);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        int coarse = best;
        bestScore = double.NegativeInfinity;
        for (int p = Math.Max(lo, coarse - 3); p <= Math.Min(hi, coarse + 3); p++)
        {
            double score = Similarity(x, natural, p, len, 1);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    private static double Similarity(float[] x, int a, int b, int len, int stride)
    {
        double xy = 0, yy = 0;
        for (int i = 0; i < len; i += stride)
        {
            double u = x[a + i], v = x[b + i];
            xy += u * v;
            yy += v * v;
        }
        return yy < 1e-12 ? (xy == 0 ? 0 : xy) : xy / Math.Sqrt(yy);
    }

    // ---------- volume ----------

    public static float[] Gain(float[] x, double factor)
    {
        var o = new float[x.Length];
        float g = (float)factor;
        for (int i = 0; i < x.Length; i++) o[i] = x[i] * g;
        return Finite(o);
    }

    /// <summary>Scales so the loudest sample sits at <paramref name="peak"/>; silence stays silence.</summary>
    public static float[] Normalize(float[] x, double peak = NormalizePeak)
    {
        double max = Peak(x);
        return max < 1e-6 ? (float[])x.Clone() : Gain(x, peak / max);
    }

    public static double Peak(float[] x)
    {
        double max = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double a = Math.Abs(x[i]);
            if (a > max) max = a;
        }
        return max;
    }

    // ---------- fades, reverse ----------

    /// <summary>From silence (the first sample is 0) up to full volume at the end of <paramref name="x"/>.</summary>
    public static float[] FadeIn(float[] x)
    {
        var o = (float[])x.Clone();
        int n = o.Length;
        if (n == 1) o[0] = 0;
        for (int i = 0; i < n && n > 1; i++) o[i] *= (float)i / (n - 1);
        return o;
    }

    /// <summary>From full volume down to silence (the last sample is 0).</summary>
    public static float[] FadeOut(float[] x)
    {
        var o = (float[])x.Clone();
        int n = o.Length;
        if (n == 1) o[0] = 0;
        for (int i = 0; i < n && n > 1; i++) o[i] *= (float)(n - 1 - i) / (n - 1);
        return o;
    }

    /// <summary>The range a fade covers when nothing is selected: the first or last half second (or the whole sound when it is shorter).</summary>
    public static (int Start, int Count) DefaultFadeRange(int total, bool fadeIn)
    {
        int n = Math.Min(total, (int)Math.Round(DefaultFadeSeconds * Sr));
        return fadeIn ? (0, n) : (total - n, n);
    }

    public static float[] Reverse(float[] x)
    {
        var o = (float[])x.Clone();
        Array.Reverse(o);
        return o;
    }

    // ---------- echo ----------

    private const double EchoDelaySeconds = 0.22;

    /// <summary>
    /// Adds repeats a little later and quieter each time. The result is longer than <paramref name="x"/> by the
    /// tail (the repeats after its end).
    /// </summary>
    public static float[] Echo(float[] x, EchoStrength strength)
    {
        double feedback = strength == EchoStrength.Strong ? 0.5 : 0.3;
        double mix = strength == EchoStrength.Strong ? 0.7 : 0.5;
        int delay = (int)Math.Round(EchoDelaySeconds * Sr);
        // Repeats until they are below about -50 dB.
        int repeats = 1;
        for (double level = mix; level > 0.003 && repeats < 20; level *= feedback) repeats++;
        int len = x.Length == 0 ? 0 : x.Length + repeats * delay;
        var d = new double[len];
        var o = new float[len];
        for (int n = 0; n < len; n++)
        {
            double dry = n < x.Length ? x[n] : 0;
            if (n >= delay)
            {
                double past = n - delay < x.Length ? x[n - delay] : 0;
                d[n] = past + feedback * d[n - delay];
            }
            o[n] = (float)(dry + mix * d[n]);
        }
        return Finite(o);
    }

    /// <summary>
    /// Echo on a range: the range gets its repeats and the tail rings on over what follows (mixed in, the sound gets
    /// longer only when the tail goes past its end).
    /// </summary>
    public static float[] EchoRange(float[] x, int start, int count, EchoStrength strength)
    {
        (start, count) = Clamp(x.Length, start, count);
        return RingOver(x, start, count, Echo(Slice(x, start, count), strength));
    }

    /// <summary>
    /// The range replaced by the first <paramref name="count"/> samples of <paramref name="wet"/>; the rest of
    /// <paramref name="wet"/> (its tail) is mixed over what follows, making the sound longer only past its end.
    /// </summary>
    private static float[] RingOver(float[] x, int start, int count, float[] wet)
    {
        int end = Math.Max(x.Length, start + wet.Length);
        var o = new float[end];
        Array.Copy(x, o, x.Length);
        for (int i = 0; i < count; i++) o[start + i] = wet[i];
        for (int i = count; i < wet.Length; i++) o[start + i] += wet[i];
        return Finite(o);
    }

    // ---------- a voice ----------

    /// <summary>Longest tail a voice may add after the sound (the longest echo + room ring for about 6 s).</summary>
    private const double MaxVoiceTailSeconds = 8;

    /// <summary>The tail is over once it has stayed under <see cref="TailFloor"/> (-60 dBFS) this long.</summary>
    private const double TailQuietSeconds = 0.5;

    private const float TailFloor = 0.001f;

    /// <summary>
    /// Runs the sound through a voice's filter, offline. The filter's own delay (the pitch shifter's) is taken out,
    /// so the result lines up with the original sample for sample. What the voice rings on with after the sound (an
    /// echo, a cave, a big room) is kept: the result is longer by that tail, its last near-silence trimmed.
    /// </summary>
    public static float[] ApplyVoice(float[] x, VoiceSliders sliders)
    {
        if (x.Length == 0) return Array.Empty<float>();
        var chain = new VoiceChain(Sr);
        chain.Apply(sliders);
        // The chain crossfades into the voice over its first 20 ms: let that happen over silence.
        int pad = Sr / 10;
        int from = pad + chain.LatencySamples;  // where the sound's first sample comes out
        int soundEnd = from + x.Length;         // ... and its last one
        int quiet = (int)(TailQuietSeconds * Sr);
        var buf = new float[soundEnd + (int)(MaxVoiceTailSeconds * Sr)];
        Array.Copy(x, 0, buf, pad, x.Length);
        int done = 0, lastLoud = -1;
        while (done < buf.Length)
        {
            var block = buf.AsSpan(done, Math.Min(AudioFormat.BlockSize, buf.Length - done));
            chain.Process(block);
            for (int k = 0; k < block.Length; k++)
                if (Math.Abs(block[k]) > TailFloor) lastLoud = done + k;
            done += block.Length;
            if (done >= soundEnd && done - lastLoud > quiet) break; // the tail has died out
        }
        int margin = (int)Math.Round(SilenceMarginSeconds * Sr);
        int end = lastLoud >= soundEnd ? Math.Min(lastLoud + 1 + margin, done) : soundEnd; // no tail: the same length
        return Finite(Slice(buf, from, end - from));
    }

    /// <summary>A voice on a range: the range takes the voice and its tail rings on over what follows (like <see cref="EchoRange"/>).</summary>
    public static float[] ApplyVoiceRange(float[] x, int start, int count, VoiceSliders sliders)
    {
        (start, count) = Clamp(x.Length, start, count);
        return RingOver(x, start, count, ApplyVoice(Slice(x, start, count), sliders));
    }

    // ---------- silence ----------

    /// <summary>Where the sound really starts and ends (first and last sample louder than the threshold, plus a 10 ms margin); (0, 0) when it is all silence.</summary>
    public static (int Start, int Count) SoundBounds(float[] x, float threshold = SilenceThreshold)
    {
        int first = -1, last = -1;
        for (int i = 0; i < x.Length; i++)
            if (Math.Abs(x[i]) > threshold) { first = i; break; }
        if (first < 0) return (0, 0);
        for (int i = x.Length - 1; i >= first; i--)
            if (Math.Abs(x[i]) > threshold) { last = i; break; }
        int margin = (int)Math.Round(SilenceMarginSeconds * Sr);
        int s = Math.Max(0, first - margin);
        int e = Math.Min(x.Length, last + 1 + margin);
        return (s, e - s);
    }

    /// <summary>Cuts the silence at the start and the end. A sound that is all silence is left as it is.</summary>
    public static float[] RemoveSilence(float[] x, float threshold = SilenceThreshold)
    {
        var (start, count) = SoundBounds(x, threshold);
        return count == 0 ? (float[])x.Clone() : Slice(x, start, count);
    }

    // ---------- helpers ----------

    /// <summary>A copy limited to -1..1 (what a sound file can hold).</summary>
    public static float[] Clamped(float[] x)
    {
        var o = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            float v = x[i];
            o[i] = float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f;
        }
        return o;
    }

    private static float[] FitLength(float[] x, int length)
    {
        if (x.Length == length) return x;
        var o = new float[length];
        Array.Copy(x, o, Math.Min(length, x.Length));
        return o;
    }

    private static float[] Finite(float[] o)
    {
        for (int i = 0; i < o.Length; i++)
            if (!float.IsFinite(o[i])) o[i] = 0;
        return o;
    }

    /// <summary>An array read as a mono stream at any rate, followed by some silence (the resampler's filter needs a tail to flush).</summary>
    private sealed class ArraySource : ISampleProvider
    {
        private readonly float[] data;
        private readonly int total;
        private int pos;

        public ArraySource(float[] data, int rate, int padding)
        {
            this.data = data;
            total = data.Length + padding;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, total - pos);
            for (int i = 0; i < n; i++)
            {
                int p = pos + i;
                buffer[offset + i] = p < data.Length ? data[p] : 0f;
            }
            pos += n;
            return n;
        }
    }
}
