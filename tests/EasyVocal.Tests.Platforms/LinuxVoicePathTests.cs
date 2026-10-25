using System.Diagnostics;
using System.Globalization;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;
using EasyVocal.Platform.Linux;
using EasyVocal.Tests.Helpers;
using Xunit.Abstractions;

namespace EasyVocal.Tests.Platforms;

/// <summary>
/// Every built-in voice through Linux' real sound path — a pretend microphone (a null sink the test plays into) read by
/// <see cref="PulseMic"/>, the real <see cref="AudioEngine"/>, <see cref="PulseSpeaker"/> into a second null sink that
/// the test records — measured against the same voice rendered offline (what Windows' engine gives too: the DSP is
/// the same code). They must sound the same and the path must not drop audio. CI only (EV_PULSE_TESTS=1, a real
/// PulseAudio server); never on a desktop.
/// </summary>
public class LinuxVoicePathTests(ITestOutputHelper output)
{
    private const int Sr = AudioFormat.SampleRate;
    private const int Block = AudioFormat.BlockSize;
    private const string In = "ev_test_in", Out = "ev_test_out";

    [Fact]
    public async Task EveryVoice_SoundsTheSameThroughPulseAudio_AsOffline()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("EV_PULSE_TESTS") != "1")
        {
            output.WriteLine("SKIPPED: needs Linux with a PulseAudio/PipeWire server and EV_PULSE_TESTS=1");
            return;
        }
        var modules = new List<string>();
        try
        {
            foreach (var sink in new[] { In, Out })
                modules.Add(Pactl("load-module", "module-null-sink", "sink_name=" + sink, "sink_properties=device.description=" + sink).Trim());

            var input = Syllables();
            var voices = BuiltInVoices.All();
            var chain = new VoiceChain(Sr);
            var mixer = new SoundMixer();
            using var engine = new AudioEngine(() => new PulseMic(null, In + ".monitor"), () => new PulseSpeaker(null, Out), () => null, chain, mixer);
            using var feeder = new PulseSpeaker(null, In);
            using var recorder = new PulseMic(null, Out + ".monitor");
            var recorded = new List<float>(Sr * 120);
            recorder.Samples += (buffer, count) => { lock (recorded) recorded.AddRange(new ReadOnlySpan<float>(buffer, 0, count)); };
            engine.FilterEnabled = true;
            chain.Apply(voices[0].Sliders);
            engine.Start();
            Assert.Equal(EngineState.Running, engine.State);
            feeder.Start();
            recorder.Start();

            // The timeline played into the pretend microphone: silence while the new sinks settle, then each voice's
            // syllables with silence around them (the voice is switched in the silence before).
            int warmup = 3 * Sr, before = Sr / 2, after = 3 * Sr / 2;
            int segment = before + input.Length + after;
            var starts = new List<int>();
            int total = warmup + voices.Count * segment;
            var play = new float[total];
            for (int v = 0; v < voices.Count; v++)
            {
                int at = warmup + v * segment + before;
                starts.Add(at);
                input.CopyTo(play, at);
            }

            // Real time, ~50 ms ahead of the clock (the speaker queues up to 500 ms).
            var clock = Stopwatch.StartNew();
            int written = 0, voice = -1;
            while (written < total)
            {
                int due = (int)(clock.Elapsed.TotalSeconds * Sr) + Sr / 20;
                while (written < due && written < total)
                {
                    int v = written < warmup ? -1 : (written - warmup) / segment;
                    if (v != voice && v >= 0) { voice = v; chain.Apply(voices[v].Sliders); }
                    int n = Math.Min(Block, total - written);
                    feeder.Write(play.AsSpan(written, n));
                    written += n;
                }
                await Task.Delay(5);
            }
            await Task.Delay(1000);
            float[] got;
            lock (recorded) got = recorded.ToArray();
            output.WriteLine($"played {total / (double)Sr:0.0} s in {clock.Elapsed.TotalSeconds:0.0} s, recorded {got.Length / (double)Sr:0.0} s");

            // Where the timeline starts in the recording: Normal (the clean voice) against what was played.
            int? found = Lag(got, play, starts[0], input.Length);
            Assert.True(found != null, "Normal's syllables were found in the recording");
            int lag = found!.Value;
            output.WriteLine($"the recording is {lag / (double)Sr * 1000:0} ms off the timeline");

            var failures = new List<string>();
            for (int v = 0; v < voices.Count; v++)
            {
                var sliders = voices[v].Sliders;
                var reference = Render(sliders, input, out int latency);
                int from = starts[v] + lag;
                if (from < 0 || from + input.Length + after > got.Length) { failures.Add(voices[v].Id + ": not recorded"); continue; }
                var real = got.AsSpan(from, input.Length + Math.Min(after, got.Length - from - input.Length)).ToArray();
                var padded = new float[reference.Length];
                Array.Copy(real, padded, Math.Min(real.Length, padded.Length));
                double ratio = Math.Pow(2, sliders.Clamp().Pitch);
                var a = VoiceFeatures.Measure(padded, PadTo(input, reference.Length), latency, ratio);
                var b = VoiceFeatures.Measure(reference, PadTo(input, reference.Length), latency, ratio);
                int gaps = Dropouts(padded, reference, input.Length + latency);
                var differences = Differences(a, b);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{voices[v].Id,-12} {(differences.Count == 0 ? "same" : string.Join(", ", differences))}  dropouts {gaps}\n   real    {a}\n   offline {b}"));
                if (differences.Count > 0) failures.Add(voices[v].Id + " sounds different: " + string.Join(", ", differences));
                if (gaps > 0) failures.Add($"{voices[v].Id}: {gaps} dropouts");
            }
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }
        finally
        {
            foreach (var m in modules)
                if (int.TryParse(m, out _)) Pactl("unload-module", m);
        }
    }

    /// <summary>
    /// What a listener would tell apart between the real path (<paramref name="a"/>) and the offline render: pitch
    /// (4 %, when both are voiced), brightness (8 %), tilt (2 dB), loudness (1.5 dB), roughness, breathiness, the room's
    /// tail (5 dB) and the octave layer (3 dB). Empty = the same voice.
    /// </summary>
    private static List<string> Differences(VoiceFeatures a, VoiceFeatures b)
    {
        var d = new List<string>();
        void Check(bool same, FormattableString what) { if (!same) d.Add(what.ToString(CultureInfo.InvariantCulture)); }
        bool voiced = a.VoicedShare > 0.3 && b.VoicedShare > 0.3 && !double.IsNaN(a.PitchHz) && !double.IsNaN(b.PitchHz);
        if (voiced) Check(Math.Abs(Math.Log(a.PitchHz / b.PitchHz)) < Math.Log(1.04), $"pitch {a.PitchHz:0} vs {b.PitchHz:0} Hz");
        Check(Math.Abs(Math.Log(a.Centroid / b.Centroid)) < Math.Log(1.08), $"brightness {a.Centroid:0} vs {b.Centroid:0} Hz");
        Check(Math.Abs(a.TiltDb - b.TiltDb) < 2, $"tilt {a.TiltDb:0.0} vs {b.TiltDb:0.0} dB");
        Check(Math.Abs(a.LevelDb - b.LevelDb) < 1.5, $"level {a.LevelDb:0.0} vs {b.LevelDb:0.0} dB");
        Check(Math.Abs(a.Roughness - b.Roughness) < 0.15, $"roughness {a.Roughness:0.00} vs {b.Roughness:0.00}");
        Check(Math.Abs(a.Flatness - b.Flatness) < 0.05, $"breathiness {a.Flatness:0.000} vs {b.Flatness:0.000}");
        Check(Math.Abs(a.TailDb - b.TailDb) < 5, $"tail {a.TailDb:0} vs {b.TailDb:0} dB");
        Check(Math.Abs(a.SubOctaveDb - b.SubOctaveDb) < 3, $"octave below {a.SubOctaveDb:0.0} vs {b.SubOctaveDb:0.0} dB");
        return d;
    }

    /// <summary>Six syllables of a vowel, its pitch gliding between 115 and 175 Hz all along (a median that doesn't jump), with pauses.</summary>
    private static float[] Syllables()
    {
        var all = new List<float>();
        double[][] vowels = { new[] { 700.0, 1200, 2600 }, new[] { 300.0, 2300, 3000 }, new[] { 500.0, 900, 2500 } };
        for (int s = 0; s < 6; s++)
        {
            double t0 = s * 0.425;
            var syllable = Signals.PulseVowel(t => 145 + 30 * Math.Sin(2 * Math.PI * (t0 + t) / 1.3), vowels[s % 3], 0.3);
            for (int n = 0; n < syllable.Length; n++) syllable[n] *= 0.5f * (float)Math.Sin(Math.PI * n / syllable.Length);
            all.AddRange(syllable);
            all.AddRange(new float[Sr / 8]);
        }
        return all.ToArray();
    }

    private static float[] Render(VoiceSliders sliders, float[] input, out int latency)
    {
        var chain = new VoiceChain(Sr);
        chain.Apply(sliders);
        latency = chain.LatencySamples;
        var o = new float[input.Length + Sr]; // room for the tail
        input.CopyTo(o, 0);
        for (int i = 0; i < o.Length; i += Block) chain.Process(o.AsSpan(i, Math.Min(Block, o.Length - i)));
        return o;
    }

    private static float[] PadTo(float[] x, int length)
    {
        var o = new float[length];
        Array.Copy(x, o, Math.Min(x.Length, length));
        return o;
    }

    /// <summary>
    /// Where the timeline is in the recording (recording index = timeline index + the result, which may be negative: the
    /// recorder may miss the first moments), from <paramref name="played"/>[start..start+length] (Normal, the clean
    /// voice): the best normalised match of their envelopes within the first seconds, then of the samples. Null: not found.
    /// </summary>
    private static int? Lag(float[] got, float[] played, int start, int length)
    {
        const int Hop = 240;
        double[] Env(float[] x, int from, int count)
        {
            var e = new double[Math.Max(0, count / Hop)];
            for (int k = 0; k < e.Length; k++)
            {
                double s = 0;
                for (int i = 0; i < Hop; i++) { int at = from + k * Hop + i; if (at >= 0 && at < x.Length) s += x[at] * x[at]; }
                e[k] = Math.Sqrt(s / Hop);
            }
            return e;
        }
        var want = Env(played, start, length);
        var have = Env(got, 0, got.Length);
        int? best = null;
        double bestScore = 0;
        for (int d = -start / Hop; d <= 3 * Sr / Hop; d++)
        {
            int at0 = start / Hop + d;
            if (at0 < 0 || at0 + want.Length > have.Length) continue;
            double s = 0, energy = 1e-12;
            for (int k = 0; k < want.Length; k++) { s += want[k] * have[at0 + k]; energy += have[at0 + k] * have[at0 + k]; }
            s /= Math.Sqrt(energy);
            if (s > bestScore) { bestScore = s; best = d * Hop; }
        }
        if (best is not int coarse) return null;
        int fine = coarse;
        bestScore = double.NegativeInfinity;
        for (int d = coarse - Hop; d <= coarse + Hop; d++)
        {
            double s = 0;
            for (int i = 0; i < length; i += 2)
            {
                int at = start + d + i;
                if (at >= 0 && at < got.Length) s += played[start + i] * got[at];
            }
            if (s > bestScore) { bestScore = s; fine = d; }
        }
        return fine;
    }

    /// <summary>
    /// Holes in the real path: 5 ms stretches where the offline render has sound and the recording (lined up with it)
    /// has next to none, as a queue that ran dry leaves. Neighbouring stretches count once.
    /// </summary>
    private static int Dropouts(float[] real, float[] reference, int length)
    {
        const int W = Sr / 200;
        int gaps = 0;
        bool inGap = false;
        for (int at = 0; at + W <= Math.Min(length, Math.Min(real.Length, reference.Length)); at += W)
        {
            double r = 0, o = 0;
            for (int i = at; i < at + W; i++) { r += real[i] * real[i]; o += reference[i] * reference[i]; }
            bool hole = o / W > 1e-4 && r < 0.01 * o;
            if (hole && !inGap) gaps++;
            inGap = hole;
        }
        return gaps;
    }

    private static string Pactl(params string[] args)
    {
        var psi = new ProcessStartInfo("pactl") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        return text;
    }
}
