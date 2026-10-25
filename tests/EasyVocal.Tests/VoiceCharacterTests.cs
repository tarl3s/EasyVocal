using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Tests.Helpers;
using Xunit.Abstractions;

namespace EasyVocal.Tests;

/// <summary>Every built-in voice run once over real speech (<see cref="Speech"/>) and measured; shared by the tests below.</summary>
public sealed class VoiceRenders
{
    private const int Sr = AudioFormat.SampleRate;
    private const int Block = AudioFormat.BlockSize;

    public VoiceRenders()
    {
        Input = Speech.Signal;
        Voices = BuiltInVoices.All();
        // Nonlinearity is measured on the first two seconds of speech: the voice at full level against a quarter of it.
        var part = Signals.Slice(Input, 0, Math.Min(2.5, Input.Length / (double)Sr));
        var quarter = part.Select(v => v * 0.25f).ToArray();
        foreach (var v in Voices)
        {
            var output = Render(v.Sliders, Input, out int latency);
            Outputs[v.Id] = output;
            var full = Render(v.Sliders, part, out _);
            var small = Render(v.Sliders, quarter, out _);
            double err = 0, ref2 = 0;
            for (int i = 0; i < full.Length; i++)
            {
                double d = full[i] - 4 * small[i];
                err += d * d;
                ref2 += (double)full[i] * full[i];
            }
            Features[v.Id] = VoiceFeatures.Measure(output, Input, latency, Math.Pow(2, v.Sliders.Clamp().Pitch))
                with { Nonlinearity = Math.Sqrt(err / Math.Max(ref2, 1e-12)) };
        }
        InputPitchHz = Features[BuiltInVoices.NormalId].PitchHz;
    }

    public float[] Input { get; }
    public IReadOnlyList<VoicePreset> Voices { get; }
    public Dictionary<string, float[]> Outputs { get; } = new();
    public Dictionary<string, VoiceFeatures> Features { get; } = new();
    /// <summary>The speech's own median pitch (Normal is the clean input).</summary>
    public double InputPitchHz { get; }

    public VoiceFeatures this[string id] => Features[id];

    public double Distance(string a, string b) => VoiceFeatures.Distance(Features[a], Features[b], InputPitchHz);

    public static float[] Render(VoiceSliders sliders, float[] input, out int latency)
    {
        var chain = new VoiceChain(Sr);
        chain.Apply(sliders);
        latency = chain.LatencySamples;
        var o = (float[])input.Clone();
        for (int i = 0; i < o.Length; i += Block) chain.Process(o.AsSpan(i, Math.Min(Block, o.Length - i)));
        return o;
    }

    public string Table()
    {
        var sb = new StringBuilder("input: " + Speech.Source + "\n");
        foreach (var v in Voices) sb.Append($"{v.Id,-12} {Features[v.Id]}\n");
        return sb.ToString();
    }
}

public class VoiceCharacterTests : IClassFixture<VoiceRenders>
{
    /// <summary>Two voices closer than this (in clearly-audible steps, see <see cref="VoiceFeatures.Distance"/>) sound alike.</summary>
    private const double MinDistance = 1.5;

    private readonly VoiceRenders r;
    private readonly ITestOutputHelper log;

    public VoiceCharacterTests(VoiceRenders renders, ITestOutputHelper log)
    {
        r = renders;
        this.log = log;
        log.WriteLine(r.Table());
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void EveryPairOfBuiltInVoices_SoundsClearlyDifferent()
    {
        var ids = r.Voices.Select(v => v.Id).ToList();
        var close = new List<string>();
        double nearest = double.MaxValue;
        string nearestPair = "";
        for (int i = 0; i < ids.Count; i++)
            for (int j = i + 1; j < ids.Count; j++)
            {
                double d = r.Distance(ids[i], ids[j]);
                if (d < nearest) { nearest = d; nearestPair = ids[i] + "/" + ids[j]; }
                if (d < MinDistance) close.Add(Inv($"{ids[i]}/{ids[j]} {d:0.00}"));
            }
        log.WriteLine(Inv($"nearest pair: {nearestPair} {nearest:0.00}"));
        Assert.True(close.Count == 0, "these voices sound alike: " + string.Join(", ", close));
    }

    [Fact]
    public void Demon_HasALayerAnOctaveBelow()
    {
        double demon = r["demon"].SubOctaveDb;
        foreach (var other in new[] { "normal", "male", "monster", "giant", "dwarf" })
            Assert.True(demon > r[other].SubOctaveDb + 4 && demon > -10, Inv($"demon sub-octave {demon:0.0} dB vs {other} {r[other].SubOctaveDb:0.0} dB"));
    }

    [Fact]
    public void Monster_IsRough()
    {
        double monster = r["monster"].Roughness;
        foreach (var v in r.Voices)
        {
            if (v.Id == "monster" || v.Sliders.Growl > 0 || v.Sliders.Octave > 0) continue; // a voice an octave down flutters at half its pitch
            Assert.True(monster > 1.5 * r[v.Id].Roughness && monster > 0.5, Inv($"monster roughness {monster:0.00} vs {v.Id} {r[v.Id].Roughness:0.00}"));
        }
    }

    [Fact]
    public void Giant_RingsTheLongest_AndIsClean()
    {
        var giant = r["giant"];
        foreach (var v in r.Voices.Where(v => v.Id != "giant"))
            Assert.True(giant.TailDb > r[v.Id].TailDb, Inv($"giant tail {giant.TailDb:0} dB vs {v.Id} {r[v.Id].TailDb:0} dB"));
        Assert.True(giant.Nonlinearity < 0.05, Inv($"giant should have no distortion: nonlinearity {giant.Nonlinearity:0.000}"));
        foreach (var rough in new[] { "demon", "monster" })
            Assert.True(r[rough].Nonlinearity > 3 * giant.Nonlinearity + 0.05, Inv($"{rough} nonlinearity {r[rough].Nonlinearity:0.000}"));
    }

    [Fact]
    public void Female_IsHigher_Male_IsLower()
    {
        double input = r.InputPitchHz;
        double female = Math.Log2(r["female"].PitchHz / input) * 12, male = Math.Log2(r["male"].PitchHz / input) * 12;
        log.WriteLine(Inv($"input {input:0} Hz, female {female:+0.0;-0.0} st, male {male:+0.0;-0.0} st"));
        Assert.InRange(female, 3, 6.5);
        Assert.InRange(male, -6, -2.5);
        Assert.True(r["female"].Centroid > r["normal"].Centroid && r["male"].Centroid < r["normal"].Centroid, "brighter female, darker male");
    }

    [Fact]
    public void TheTwoRobots_AreDifferent()
    {
        var robot = r["robot"];
        var movie = r["movie-robot"];
        Assert.True(r.Distance("robot", "movie-robot") >= 1.5, Inv($"robot/movie-robot distance {r.Distance("robot", "movie-robot"):0.00}"));
        Assert.True(robot.PitchSpread < 0.5 * r["normal"].PitchSpread, Inv($"robot pitch spread {robot.PitchSpread:0.00} vs normal {r["normal"].PitchSpread:0.00}"));
        Assert.InRange(movie.PitchHz, VocoderEffect.CarrierHz * 0.92, VocoderEffect.CarrierHz * 1.08);
        Assert.True(movie.PitchSpread < 0.05, Inv($"movie robot is one note: spread {movie.PitchSpread:0.000}"));
    }

    [Fact]
    public void Ghost_IsBreathyAndWavers()
    {
        var ghost = r["ghost"];
        Assert.True(ghost.Flatness > 2 * r["normal"].Flatness, Inv($"ghost flatness {ghost.Flatness:0.000} vs normal {r["normal"].Flatness:0.000}"));
        Assert.True(ghost.TailDb > r["normal"].TailDb + 20, Inv($"ghost tail {ghost.TailDb:0} dB"));
    }

    /// <summary>
    /// Listening files for people (we can't listen): set EV_VOICE_SAMPLES to a folder and run this test; it writes
    /// the speech, every built-in voice on it and a README. A "my-voice.wav" put in that folder is rendered too.
    /// </summary>
    [Fact]
    public void RenderListeningSamples()
    {
        string? dir = Environment.GetEnvironmentVariable("EV_VOICE_SAMPLES");
        if (string.IsNullOrWhiteSpace(dir))
        {
            log.WriteLine("Set EV_VOICE_SAMPLES to a folder to write the listening samples.");
            return;
        }
        Directory.CreateDirectory(dir);
        WavFile.WriteMono(Path.Combine(dir, "00 input (speech).wav"), r.Input);
        string mine = Path.Combine(dir, "my-voice.wav");
        float[]? myVoice = File.Exists(mine) ? WavFile.ReadMono(mine).Concat(new float[2 * AudioFormat.SampleRate]).ToArray() : null;
        int n = 1;
        foreach (var v in r.Voices)
        {
            string name = $"{n++:00} {v.Id}";
            WavFile.WriteMono(Path.Combine(dir, name + ".wav"), Limit(r.Outputs[v.Id]));
            if (myVoice != null)
                WavFile.WriteMono(Path.Combine(dir, name + " (my voice).wav"), Limit(VoiceRenders.Render(v.Sliders, myVoice, out _)));
        }
        File.WriteAllText(Path.Combine(dir, "README.txt"), ListeningReadme(r), new UTF8Encoding(false));
        log.WriteLine("wrote the listening samples to " + dir);
    }

    private static float[] Limit(float[] x)
    {
        var o = (float[])x.Clone();
        var limiter = new Limiter(AudioFormat.SampleRate);
        for (int i = 0; i < o.Length; i += AudioFormat.BlockSize)
            limiter.Process(o.AsSpan(i, Math.Min(AudioFormat.BlockSize, o.Length - i)));
        return o;
    }

    /// <summary>What each voice should sound like (the same words as the README's voice list).</summary>
    public static readonly IReadOnlyDictionary<string, string> Signatures = new Dictionary<string, string>
    {
        ["normal"] = "your own voice, untouched",
        ["female"] = "about 4-5 semitones higher with a smaller, lighter voice body and less low end",
        ["male"] = "about 4 semitones deeper with a bigger voice body and more chest",
        ["robot"] = "your melody flattened to almost one note, metallic and buzzing (ring modulation)",
        ["chipmunk"] = "very high and tiny, cartoon-fast feeling",
        ["monster"] = "deep, rough and rattling (growl), heavy bass, almost no room",
        ["alien"] = "a bit higher with a wavering, warbling pitch, a metallic edge and a spacey echo",
        ["radio"] = "thin old-radio band (telephone-like), a little crunch and crackle",
        ["demon"] = "your voice plus a second voice an octave below it, distorted, in a dark cave",
        ["cave"] = "your normal voice with echoes off rock walls",
        ["dwarf"] = "a little deeper but with a small, gruff, slightly rough body",
        ["giant"] = "a huge, slow-feeling body, moderately deeper, in an enormous hall; clean (no distortion)",
        ["ghost"] = "an airy whisper with a slight waver, floating in a big soft room",
        ["movie-robot"] = "a synthesizer buzz on one fixed note that says your words (vocoder), slightly radio-like",
    };

    private static string ListeningReadme(VoiceRenders r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("EasyVocal - listening samples of the built-in voices");
        sb.AppendLine("(made by the tests; this folder is git-ignored)");
        sb.AppendLine();
        sb.AppendLine("00 input (speech).wav = the dry input: " + Speech.Source);
        sb.AppendLine("Put your own recording here as my-voice.wav and run the test again to hear every voice on it.");
        sb.AppendLine();
        int n = 1;
        foreach (var v in r.Voices)
            sb.AppendLine($"{n++:00} {v.Id,-12} {v.Name}: {(Signatures.TryGetValue(v.Id, out var s) ? s : "")}");
        sb.AppendLine();
        sb.AppendLine("Measured (pitch, spread, voiced share, brightness, tilt, noisiness, roughness, room tail, octave-below energy, distortion):");
        sb.Append(r.Table());
        return sb.ToString();
    }

    [Fact]
    public void EveryBuiltInVoice_KeepsAboutTheSameLoudness()
    {
        foreach (var v in r.Voices)
            Assert.True(r[v.Id].LevelDb is > -6 and < 5, Inv($"{v.Id} is {r[v.Id].LevelDb:+0.0;-0.0} dB against the input"));
    }

    [Fact]
    public void EveryBuiltInVoice_HasASignatureInTheReadme()
    {
        foreach (var v in r.Voices) Assert.True(Signatures.ContainsKey(v.Id), v.Id + " has no description");
    }
}
