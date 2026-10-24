using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

public class VoiceChainTests
{
    private const int Sr = AudioFormat.SampleRate;
    private const int Block = AudioFormat.BlockSize;
    private static readonly double[] Formants = { 700.0, 1200.0, 2600.0 };

    private static VoiceSliders Preset(string id) =>
        BuiltInVoices.All().First(v => v.Id == id).Sliders.Clone();

    /// <summary>Runs a copy of the signal through the chain in 10 ms blocks; <paramref name="atBlock"/> may change the chain before each block.</summary>
    private static float[] Run(VoiceChain chain, float[] input, Action<int>? atBlock = null)
    {
        var o = (float[])input.Clone();
        int b = 0;
        for (int i = 0; i < o.Length; i += Block, b++)
        {
            atBlock?.Invoke(b);
            chain.Process(o.AsSpan(i, Math.Min(Block, o.Length - i)));
        }
        return o;
    }

    private static double MaxStep(float[] x, int from = 1)
    {
        double m = 0;
        for (int n = Math.Max(1, from); n < x.Length; n++) m = Math.Max(m, Math.Abs(x[n] - x[n - 1]));
        return m;
    }

    [Fact]
    public void NeutralSliders_AreBitExactIdentity()
    {
        var chain = new VoiceChain(Sr);
        chain.Apply(new VoiceSliders());
        var input = Signals.PulseVowel(150, Formants, 1);
        var output = Run(chain, input);
        Assert.Equal(input, output);
        Assert.Equal(0, chain.LatencySamples);
    }

    [Fact]
    public void Bypass_IsBitExactIdentity_EvenWithAPresetApplied()
    {
        var chain = new VoiceChain(Sr);
        chain.Apply(Preset("demon"));
        var input = Signals.PulseVowel(150, Formants, 1);
        var output = Run(chain, input, b => { if (b == 50) chain.Bypass = true; });
        int from = 50 * Block + Sr / 10; // the 20 ms fade is long over
        Assert.Equal(input.AsSpan(from).ToArray(), output.AsSpan(from).ToArray());
        Assert.Equal(0, chain.LatencySamples);
    }

    [Fact]
    public void ChangingBackToNeutral_ReturnsTheCleanInput()
    {
        var chain = new VoiceChain(Sr);
        chain.Apply(Preset("demon"));
        var input = Signals.PulseVowel(150, Formants, 1);
        var output = Run(chain, input, b => { if (b == 50) chain.Apply(new VoiceSliders()); });
        int from = 50 * Block + Sr / 10;
        Assert.Equal(input.AsSpan(from).ToArray(), output.AsSpan(from).ToArray());
    }

    [Fact]
    public void BypassOff_BringsTheVoiceBack()
    {
        var chain = new VoiceChain(Sr);
        chain.Bypass = true;
        chain.Apply(Preset("female"));
        var input = Signals.PulseVowel(150, Formants, 2);
        var output = Run(chain, input, b => { if (b == 100) chain.Bypass = false; });
        Assert.InRange(Signals.EstimatePitch(Signals.Slice(output, 1.4, 2)), 180, 260);
    }

    [Fact]
    public void SwitchingPresets_NoClicks()
    {
        var chain = new VoiceChain(Sr);
        var input = Signals.Sine(220, 2.5, 0.5f);
        var cycle = new[] { "normal", "demon", "chipmunk", "normal" };
        var output = Run(chain, input, b =>
        {
            if (b % 50 == 0) chain.Apply(Preset(cycle[Math.Min(b / 50, cycle.Length - 1)]));
        });
        Assert.True(Signals.AllFinite(output));
        Assert.True(MaxStep(output) < 0.3, "max step " + MaxStep(output));
    }

    [Fact]
    public void DraggingSliders_NoClicks()
    {
        var chain = new VoiceChain(Sr);
        var s = new VoiceSliders { Pitch = 0.25, Body = 0.1 };
        chain.Apply(s);
        var input = Signals.Sine(220, 4, 0.5f);
        var output = Run(chain, input, b =>
        {
            // One slider after another (not Whisper: that one is noise), 0.05 per 10 ms block, up and back down.
            int step = b / 20; // 20 blocks = 1.0
            double v = (b % 20) * 0.05;
            if (step >= 10) return;
            if (step % 2 == 1) v = 1 - v;
            var t = s.Clone();
            switch (step / 2)
            {
                case 0: t.Robot = v; break;
                case 1: t.Distortion = v; break;
                case 2: t.Radio = v; break;
                case 3: t.Echo = v; break;
                case 4: t.Pitch = 0.25 + 0.5 * v; break;
            }
            chain.Apply(t);
        });
        Assert.True(Signals.AllFinite(output));
        Assert.True(MaxStep(output, 480) < 0.3, "max step " + MaxStep(output, 480));
    }

    [Fact]
    public void DraggingWhisper_ChangesLevelGradually()
    {
        var chain = new VoiceChain(Sr);
        var s = new VoiceSliders { Pitch = 0.25 };
        chain.Apply(s);
        var input = Signals.PulseVowel(150, Formants, 3);
        var output = Run(chain, input, b =>
        {
            if (b < 100 || b >= 120) return;
            s.Whisper = (b - 99) * 0.05;
            chain.Apply(s.Clone());
        });
        Assert.True(Signals.AllFinite(output));
        double worst = 0;
        for (int b = 100; b < 130; b++)
        {
            double a = Signals.Rms(Signals.Slice(output, b * 0.01, (b + 1) * 0.01));
            double p = Signals.Rms(Signals.Slice(output, (b - 1) * 0.01, b * 0.01));
            worst = Math.Max(worst, Math.Abs(a - p));
        }
        Assert.True(worst < 0.08, "level jump " + worst);
    }

    [Fact]
    public void EffectComingBackFromZero_DoesNotReplayAStaleTail()
    {
        var chain = new VoiceChain(Sr);
        var s = new VoiceSliders { Pitch = 0.1, Echo = 0.5 };
        chain.Apply(s);
        var voice = Signals.PulseVowel(150, Formants, 1);
        var silence = Signals.Silence(1.5);
        var input = voice.Concat(silence).Concat(silence).ToArray();
        int fadeOutAt = 100, backAt = 100 + 150; // 1 s of voice, echo down to 0 in small steps, then up again
        var output = Run(chain, input, b =>
        {
            if (b >= fadeOutAt && b < fadeOutAt + 10) { s.Echo = 0.5 - 0.05 * (b - fadeOutAt + 1); chain.Apply(s.Clone()); }
            if (b >= backAt && b < backAt + 10) { s.Echo = 0.05 * (b - backAt + 1); chain.Apply(s.Clone()); }
        });
        double peak = Signals.Peak(Signals.Slice(output, (backAt + 12) * Block / (double)Sr, 4.0));
        Assert.True(peak < 1e-3, "stale tail peak " + peak);
    }

    [Fact]
    public void EveryBuiltIn_FiniteOnVoiceAndSilence()
    {
        var voice = Signals.PulseVowel(150, Formants, 2);
        foreach (var preset in BuiltInVoices.All())
        {
            var chain = new VoiceChain(Sr);
            chain.Apply(preset.Sliders);
            var input = voice.Concat(Signals.Silence(1)).ToArray();
            var output = Run(chain, input);
            Assert.True(Signals.AllFinite(output), preset.Id + " is not finite");
            Assert.True(Signals.Peak(output) < 4, preset.Id + " is absurdly loud");

            var tail = Run(chain, Signals.Silence(3));
            Assert.True(Signals.AllFinite(tail), preset.Id + " tail is not finite");
            double peak = Signals.Peak(Signals.Slice(tail, 2.7, 3.0));
            Assert.True(peak < 1e-3, preset.Id + " tail " + peak);
        }
    }

    [Fact]
    public void Female_RaisesPitch_Male_LowersIt()
    {
        var input = Signals.PulseVowel(150, Formants, 2);
        var female = new VoiceChain(Sr);
        female.Apply(Preset("female"));
        Assert.True(Signals.EstimatePitch(Signals.Slice(Run(female, input), 1, 2)) > 180);
        var male = new VoiceChain(Sr);
        male.Apply(Preset("male"));
        double hz = Signals.EstimatePitch(Signals.Slice(Run(male, input), 1, 2));
        Assert.InRange(hz, 1, 125);
    }

    [Fact]
    public void LatencyBudget_AtMost30ms()
    {
        foreach (var preset in BuiltInVoices.All())
        {
            var chain = new VoiceChain(Sr);
            chain.Apply(preset.Sliders);
            Assert.True(chain.LatencySamples <= 1440, preset.Id + " latency " + chain.LatencySamples);
        }
        Assert.True(new Limiter(Sr).LatencySamples <= Sr / 500, "limiter look-ahead above 2 ms");
    }

    [Fact]
    public void Performance_HeaviestPreset_UnderFivePercentCpu()
    {
#if DEBUG
        Console.WriteLine("Performance test skipped: needs a Release build (dotnet test -c Release).");
#else
        var chain = new VoiceChain(Sr);
        chain.Apply(Preset("demon"));
        var input = Signals.PulseVowel(150, Formants, 10);
        var warm = (float[])input.Clone();
        for (int i = 0; i + Block <= 24000; i += Block) chain.Process(warm.AsSpan(i, Block));
        var sw = Stopwatch.StartNew();
        for (int i = 0; i + Block <= input.Length; i += Block) chain.Process(input.AsSpan(i, Block));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, "10 s of audio took " + sw.ElapsedMilliseconds + " ms");
#endif
    }

    /// <summary>
    /// Bytes the audio thread allocated, least over a few attempts. The counter is per thread but is only exact
    /// to an allocation chunk, so a garbage collection on another test's thread can add one phantom chunk; a real
    /// allocation in the chain shows up in every attempt.
    /// </summary>
    private static long AllocatedBytes(Func<long> attempt)
    {
        long best = long.MaxValue;
        for (int k = 0; k < 6 && best != 0; k++) best = Math.Min(best, attempt());
        return best;
    }

    [Fact]
    public void Process_DoesNotAllocate()
    {
        var chain = new VoiceChain(Sr);
        chain.Apply(Preset("demon"));
        var block = Signals.PulseVowel(150, Formants, 0.01).AsSpan(0, Block).ToArray();
        var work = new float[Block];
        for (int i = 0; i < 100; i++) { block.CopyTo(work, 0); chain.Process(work); } // warm up the JIT
        long allocated = AllocatedBytes(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { block.CopyTo(work, 0); chain.Process(work); }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Process_DoesNotAllocate_WhenSwitchingVoices()
    {
        var chain = new VoiceChain(Sr);
        var voices = new[] { Preset("demon"), Preset("chipmunk"), new VoiceSliders(), Preset("cave") };
        var block = Signals.PulseVowel(150, Formants, 0.01).AsSpan(0, Block).ToArray();
        var work = new float[Block];
        long Attempt()
        {
            long total = 0;
            for (int i = 0; i < 400; i++)
            {
                if (i % 20 == 0) chain.Apply(voices[(i / 20) % voices.Length]); // Apply allocates its snapshot, on the UI thread
                block.CopyTo(work, 0);
                long before = GC.GetAllocatedBytesForCurrentThread();
                chain.Process(work);
                total += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            return total;
        }
        Attempt(); // warm up every code path
        Assert.Equal(0, AllocatedBytes(Attempt));
    }


    [Fact]
    public void Limiter_KeepsPeaksBelowOne()
    {
        var loud = Signals.Sine(220, 1, 3.0f);
        var limited = Signals.Run(new LimiterEffect(new Limiter(Sr)), loud);
        Assert.True(Signals.Peak(limited) <= 0.98 + 1e-3, "peak " + Signals.Peak(limited));
        Assert.True(Signals.Rms(Signals.Slice(limited, 0.5, 1)) > 0.5, "limiter squashed the signal");

        var quiet = Signals.Sine(220, 1, 0.2f);
        var lim = new Limiter(Sr);
        var o = Signals.Run(new LimiterEffect(lim), quiet);
        int d = lim.LatencySamples;
        double err = 0;
        for (int n = d; n < o.Length; n++) err = Math.Max(err, Math.Abs(o[n] - quiet[n - d]));
        Assert.True(err < 1e-3, "error " + err);
    }

    [Fact]
    public void Limiter_ReleasesSlowly_AndStaysSafeOnSpikes()
    {
        var input = Signals.Sine(220, 1, 0.3f);
        input[24000] = 5f; // one lone spike
        input[24001] = -5f;
        var o = Signals.Run(new LimiterEffect(new Limiter(Sr)), input);
        Assert.True(Signals.Peak(o) <= 0.98 + 1e-3);
        Assert.True(Signals.AllFinite(o));
        // Long after the spike the gain is back.
        Assert.True(Signals.Rms(Signals.Slice(o, 0.9, 1)) > 0.95 * Signals.Rms(Signals.Slice(input, 0.9, 1)));
    }

    /// <summary>Lets the test helpers drive a <see cref="Limiter"/> like an effect.</summary>
    private sealed class LimiterEffect : IAudioEffect
    {
        private readonly Limiter limiter;
        public LimiterEffect(Limiter limiter) { this.limiter = limiter; }
        public void Process(Span<float> mono) => limiter.Process(mono);
        public void Reset() { }
    }

    [Fact(Skip = "Manual: put a recorded voice at %TEMP%\\EV_presets\\input.wav, un-skip locally, and listen to the results.")]
    public void RenderPresets()
    {
        string dir = Path.Combine(Path.GetTempPath(), "EV_presets");
        var input = WavFile.ReadMono(Path.Combine(dir, "input.wav"));
        foreach (var preset in BuiltInVoices.All())
        {
            var chain = new VoiceChain(Sr);
            chain.Apply(preset.Sliders);
            var output = Run(chain, input.Concat(Signals.Silence(2)).ToArray());
            var limiter = new Limiter(Sr);
            for (int i = 0; i < output.Length; i += Block)
                limiter.Process(output.AsSpan(i, Math.Min(Block, output.Length - i)));
            WavFile.WriteMono(Path.Combine(dir, preset.Id + ".wav"), output);
        }
    }

    /// <summary>Just enough WAV for <see cref="RenderPresets"/>: 16-bit PCM or 32-bit float in, 32-bit float out.</summary>
    private static class WavFile
    {
        public static float[] ReadMono(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            r.ReadBytes(12);
            int channels = 1, rate = Sr, bits = 16, format = 1;
            while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
            {
                string id = new string(r.ReadChars(4));
                int size = r.ReadInt32();
                if (id == "fmt ")
                {
                    format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32();
                    r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                    r.ReadBytes(size - 16);
                }
                else if (id == "data")
                {
                    int frames = size / (bits / 8) / channels;
                    var mono = new float[frames];
                    for (int n = 0; n < frames; n++)
                    {
                        float sum = 0;
                        for (int c = 0; c < channels; c++)
                            sum += format == 3 ? r.ReadSingle() : bits == 16 ? r.ReadInt16() / 32768f : throw new NotSupportedException("use a 16-bit or float WAV");
                        mono[n] = sum / channels;
                    }
                    if (rate == Sr) return mono;
                    var resampled = new float[(int)((long)frames * Sr / rate)];
                    for (int n = 0; n < resampled.Length; n++)
                    {
                        double p = (double)n * rate / Sr;
                        int i = (int)p;
                        float f = (float)(p - i);
                        resampled[n] = mono[i] + (mono[Math.Min(i + 1, frames - 1)] - mono[i]) * f;
                    }
                    return resampled;
                }
                else r.ReadBytes(size + (size & 1));
            }
            throw new InvalidDataException("no data chunk");
        }

        public static void WriteMono(string path, float[] x)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write("RIFF".ToCharArray()); w.Write(36 + x.Length * 4); w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(Sr);
            w.Write(Sr * 4); w.Write((short)4); w.Write((short)32);
            w.Write("data".ToCharArray()); w.Write(x.Length * 4);
            foreach (float v in x) w.Write(v);
        }
    }
}
