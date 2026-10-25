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
    public void EffectsWithoutAShift_AddNoLatency()
    {
        var shifterDelay = new VoiceShifter(Sr).LatencySamples;
        Assert.True(shifterDelay > 0);
        foreach (var id in new[] { "radio", "cave", "movie-robot" })
        {
            var chain = new VoiceChain(Sr);
            chain.Apply(Preset(id));
            Assert.Equal(0, chain.LatencySamples);
        }
        var female = new VoiceChain(Sr);
        female.Apply(Preset("female"));
        Assert.Equal(shifterDelay, female.LatencySamples);
        var pitchOnly = new VoiceChain(Sr);
        pitchOnly.Apply(new VoiceSliders { Body = 0.2 });
        Assert.Equal(shifterDelay, pitchOnly.LatencySamples);
    }

    [Fact]
    public void RobotOnly_OutputIsNotDelayedByTheShifter()
    {
        // An impulse through the robot effect comes out at once (the robot multiplies, the echo only adds later).
        var chain = new VoiceChain(Sr);
        chain.Apply(new VoiceSliders { Robot = 0.8, Echo = 0.1 }); // the robot effect alone (the Robot voice also flattens the pitch)
        Run(chain, Signals.Sine(200, 0.2, 0.3f)); // let the 20 ms fade from clean finish
        Run(chain, Signals.Silence(0.5));         // and every tail die out
        var input = new float[Sr / 10];
        input[100] = 0.8f;
        var output = Run(chain, input);
        for (int n = 0; n < 100; n++) Assert.True(Math.Abs(output[n]) < 1e-3, $"sample {n}: {output[n]}");
        Assert.True(Math.Abs(output[100]) > 0.05, "the click should come out right away: " + output[100]);
    }

    [Fact]
    public void SwitchingRobotToFemaleAndBack_NoClicks()
    {
        var chain = new VoiceChain(Sr);
        var input = Signals.Sine(220, 2, 0.5f);
        var cycle = new[] { "robot", "female", "robot", "cave", "male" };
        var output = Run(chain, input, b =>
        {
            if (b % 40 == 0) chain.Apply(Preset(cycle[Math.Min(b / 40, cycle.Length - 1)]));
        });
        Assert.True(Signals.AllFinite(output));
        Assert.True(MaxStep(output, 480) < 0.3, "max step " + MaxStep(output, 480));
    }

    [Fact]
    public void DraggingPitchAwayFromTheMiddle_WithRobotOn_NoClicks()
    {
        var chain = new VoiceChain(Sr);
        var sliders = new VoiceSliders { Robot = 0.8, Echo = 0.1 };
        chain.Apply(sliders);
        var input = Signals.Sine(220, 1.5, 0.5f);
        var output = Run(chain, input, b =>
        {
            if (b >= 30 && b < 90)
            {
                sliders.Pitch = (b - 30) / 60.0 * 0.5; // the shifter comes in on the first step off 0
                chain.Apply(sliders.Clone());
            }
        });
        Assert.True(Signals.AllFinite(output));
        Assert.True(MaxStep(output, 480) < 0.3, "max step " + MaxStep(output, 480));
    }

    [Fact]
    public void Performance_HeaviestPreset_UnderFivePercentCpu()
    {
#if DEBUG
        Console.WriteLine("Performance test skipped: needs a Release build (dotnet test -c Release).");
#else
        var input = Signals.PulseVowel(150, Formants, 10);
        long worst = 0;
        string heaviest = "";
        foreach (var preset in BuiltInVoices.All())
        {
            var chain = new VoiceChain(Sr);
            chain.Apply(preset.Sliders);
            var warm = (float[])input.Clone();
            for (int i = 0; i + Block <= 24000; i += Block) chain.Process(warm.AsSpan(i, Block));
            var work = (float[])input.Clone();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i + Block <= work.Length; i += Block) chain.Process(work.AsSpan(i, Block));
            sw.Stop();
            if (sw.ElapsedMilliseconds >= worst) { worst = sw.ElapsedMilliseconds; heaviest = preset.Id; }
        }
        Console.WriteLine($"heaviest voice: {heaviest}, 10 s of audio in {worst} ms");
        Assert.True(worst < 500, $"10 s of audio took {worst} ms with {heaviest}");
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
        for (int k = 0; k < 6 && best != 0; k++)
        {
            if (k > 0) Thread.Sleep(250); // lets the JIT finish moving hot methods to their faster code (a busy runner is slow at it)
            best = Math.Min(best, attempt());
        }
        return best;
    }

    [Fact]
    public void Process_DoesNotAllocate()
    {
        var block = Signals.PulseVowel(150, Formants, 0.01).AsSpan(0, Block).ToArray();
        var work = new float[Block];
        foreach (var preset in BuiltInVoices.All())
        {
            var chain = new VoiceChain(Sr);
            chain.Apply(preset.Sliders);
            for (int i = 0; i < 100; i++) { block.CopyTo(work, 0); chain.Process(work); } // warm up the JIT
            long allocated = AllocatedBytes(() =>
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) { block.CopyTo(work, 0); chain.Process(work); }
                return GC.GetAllocatedBytesForCurrentThread() - before;
            });
            Assert.True(allocated == 0, preset.Id + " allocated " + allocated + " bytes");
        }
    }

    [Fact]
    public void Process_DoesNotAllocate_WhenSwitchingVoices()
    {
        var chain = new VoiceChain(Sr);
        var voices = new[] { Preset("demon"), Preset("chipmunk"), new VoiceSliders(), Preset("cave"), Preset("movie-robot"), Preset("monster"), Preset("ghost") };
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
}
