using System;
using System.IO;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;
using EasyVocal.Tests.Helpers;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace EasyVocal.Tests;

public class SoundTests : IDisposable
{
    private const int Sr = AudioFormat.SampleRate;
    private const int Block = AudioFormat.BlockSize;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EV_sounds_" + Guid.NewGuid().ToString("N"));

    public SoundTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    // ---------- loader ----------

    [Fact]
    public void Load_Wav44kStereo_ReturnsMono48k()
    {
        string path = Path_("tone.wav");
        using (var w = new WaveFileWriter(path, new WaveFormat(44100, 16, 2)))
        {
            var frame = new float[2];
            for (int n = 0; n < 44100; n++)
            {
                frame[0] = frame[1] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * n / 44100.0));
                w.WriteSamples(frame, 0, 2);
            }
        }
        var x = SoundLoader.Load(path);
        Assert.InRange(x.Length, 48000 - 10, 48000 + 10);
        Assert.InRange(Signals.EstimatePitch(x), 435, 445);
        Assert.InRange(Signals.Peak(x), 0.45, 0.55);
    }

    [Fact]
    public void Load_Mp3_Works()
    {
        string wav = Path_("src.wav"), mp3 = Path_("tone.mp3");
        using (var w = new WaveFileWriter(wav, new WaveFormat(44100, 16, 1)))
        {
            var s = new float[44100 / 2];
            for (int n = 0; n < s.Length; n++) s[n] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * n / 44100.0));
            w.WriteSamples(s, 0, s.Length);
        }
        try
        {
            MediaFoundationApi.Startup();
            using var reader = new WaveFileReader(wav);
            MediaFoundationEncoder.EncodeToMp3(reader, mp3);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Skipped: no MP3 encoder on this machine (" + ex.Message + ")");
            return;
        }
        var x = SoundLoader.Load(mp3);
        Assert.InRange(x.Length, Sr / 2 - 4000, Sr / 2 + 4000); // MP3 adds padding
        Assert.InRange(Signals.EstimatePitch(x), 430, 450);
    }

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".wav")]
    [InlineData(".ogg")]
    public void Load_CorruptFile_ThrowsSoundLoadException(string ext)
    {
        string path = Path_("bad" + ext);
        var bytes = new byte[5000];
        new Random(1).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        var ex = Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Load_UnknownExtension_ThrowsSoundLoadException()
    {
        string path = Path_("notes.txt");
        File.WriteAllText(path, "hello");
        Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path));
    }

    [Fact]
    public void Load_MissingFile_ThrowsSoundLoadException()
    {
        Assert.Throws<SoundLoadException>(() => SoundLoader.Load(Path_("nope.wav")));
    }

    [Fact]
    public void Load_ZeroSamples_ThrowsSoundLoadException()
    {
        string path = Path_("empty.wav");
        using (new WaveFileWriter(path, new WaveFormat(48000, 16, 1))) { }
        Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path));
    }

    [Fact]
    public void Load_TooLong_ThrowsSoundLoadException()
    {
        string path = Path_("long.wav");
        using (var w = new WaveFileWriter(path, new WaveFormat(8000, 16, 1)))
        {
            var silence = new byte[8000 * 2];
            for (int s = 0; s < 601; s++) w.Write(silence, 0, silence.Length);
        }
        var ex = Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path));
        Assert.Contains("long", ex.Message);
    }

    // ---------- mixer ----------

    private static float[] Constant(float value, int length)
    {
        var x = new float[length];
        Array.Fill(x, value);
        return x;
    }

    private static float[] MixBlock(SoundMixer mixer)
    {
        var b = new float[Block];
        mixer.MixInto(b);
        return b;
    }

    [Fact]
    public void Mixer_ToggleTwice_Stops()
    {
        var m = new SoundMixer();
        m.Set("a", Constant(0.2f, Sr));
        Assert.True(m.Toggle("a", 1));
        Assert.True(m.IsPlaying("a"));
        Assert.Equal(0.2f, MixBlock(m)[0], 5);
        Assert.False(m.Toggle("a", 1));
        Assert.False(m.IsPlaying("a"));
        Assert.Equal(0, Signals.Peak(MixBlock(m)));
    }

    [Fact]
    public void Mixer_ToggleUnknownSound_DoesNothing()
    {
        var m = new SoundMixer();
        Assert.False(m.Toggle("ghost", 1));
        Assert.False(m.IsPlaying("ghost"));
    }

    [Fact]
    public void Mixer_TwoSoundsOverlap_AddUp()
    {
        var m = new SoundMixer();
        m.Set("a", Constant(0.2f, Sr));
        m.Set("b", Constant(0.2f, Sr));
        m.Toggle("a", 1);
        m.Toggle("b", 1);
        var b = MixBlock(m);
        Assert.Equal(0.4f, b[0], 5);
        Assert.Equal(0.4f, b[Block - 1], 5);
    }

    [Fact]
    public void Mixer_AddsOnTopOfWhatIsAlreadyThere()
    {
        var m = new SoundMixer();
        m.Set("a", Constant(0.2f, Sr));
        m.Toggle("a", 1);
        var b = Constant(0.1f, Block);
        m.MixInto(b);
        Assert.Equal(0.3f, b[10], 5);
    }

    [Fact]
    public void Mixer_Volume_Scales()
    {
        var m = new SoundMixer();
        m.Set("a", Constant(0.2f, Sr));
        m.Toggle("a", 0.5f);
        Assert.Equal(0.1f, MixBlock(m)[0], 5);
    }

    [Fact]
    public void Mixer_SoundEnds_RaisesPlayingChangedFalse()
    {
        var m = new SoundMixer();
        var events = new System.Collections.Generic.List<(string, bool)>();
        m.PlayingChanged += (id, on) => events.Add((id, on));
        m.Set("a", Constant(0.2f, Block + 100));
        m.Toggle("a", 1);
        MixBlock(m);
        Assert.Equal(new[] { ("a", true) }, events);
        Assert.True(m.IsPlaying("a"));
        var second = MixBlock(m); // 100 samples left, then silence
        Assert.Equal(0.2f, second[99], 5);
        Assert.Equal(0f, second[100]);
        Assert.Equal(new[] { ("a", true), ("a", false) }, events);
        Assert.False(m.IsPlaying("a"));
        Assert.True(m.Toggle("a", 1)); // can be played again
    }

    [Fact]
    public void Mixer_StopAll_SilencesEverything()
    {
        var m = new SoundMixer();
        int stopped = 0;
        m.PlayingChanged += (_, on) => { if (!on) stopped++; };
        m.Set("a", Constant(0.2f, Sr));
        m.Set("b", Constant(0.2f, Sr));
        m.Toggle("a", 1);
        m.Toggle("b", 1);
        MixBlock(m);
        m.StopAll();
        Assert.False(m.IsPlaying("a"));
        Assert.False(m.IsPlaying("b"));
        Assert.Equal(0, Signals.Peak(MixBlock(m)));
        Assert.Equal(2, stopped);
    }

    [Fact]
    public void Mixer_StopAllThenToggle_PlaysAgain()
    {
        var m = new SoundMixer();
        m.Set("a", Constant(0.2f, Sr));
        m.Toggle("a", 1);
        m.StopAll();
        Assert.True(m.Toggle("a", 1));
        Assert.Equal(0.2f, MixBlock(m)[0], 5);
    }

    [Fact]
    public void Mixer_MoreThan16Voices_OldestStops()
    {
        var m = new SoundMixer();
        var stopped = new System.Collections.Generic.List<string>();
        m.PlayingChanged += (id, on) => { if (!on) stopped.Add(id); };
        for (int i = 0; i < 17; i++)
        {
            m.Set("s" + i, Constant(0.01f, Sr));
            m.Toggle("s" + i, 1);
        }
        var b = MixBlock(m);
        Assert.Equal(0.16f, b[0], 4); // 16 voices
        Assert.Equal(new[] { "s0" }, stopped);
        Assert.False(m.IsPlaying("s0"));
        Assert.True(m.IsPlaying("s16"));
    }

    [Fact]
    public void Mixer_SetWhilePlaying_KeepsPlayingOldSamples()
    {
        var m = new SoundMixer();
        m.Set("a", Constant(0.2f, Sr));
        m.Toggle("a", 1);
        MixBlock(m);
        m.Set("a", Constant(0.9f, Sr));
        Assert.Equal(0.2f, MixBlock(m)[0], 5);
        m.Toggle("a", 1); // stop
        m.Toggle("a", 1); // start again with the new samples
        Assert.Equal(0.9f, MixBlock(m)[0], 5);
    }

    [Fact]
    public void Mixer_RemoveWhilePlaying_StopsIt()
    {
        var m = new SoundMixer();
        m.Set("a", Constant(0.2f, Sr));
        m.Toggle("a", 1);
        MixBlock(m);
        m.Remove("a");
        Assert.False(m.IsPlaying("a"));
        Assert.Equal(0, Signals.Peak(MixBlock(m)));
        Assert.False(m.Toggle("a", 1));
    }

    private static long AllocatedBytes(Func<long> attempt)
    {
        long best = long.MaxValue;
        for (int k = 0; k < 6 && best != 0; k++) best = Math.Min(best, attempt());
        return best;
    }

    [Fact]
    public void Mixer_MixInto_DoesNotAllocate()
    {
        var m = new SoundMixer();
        int events = 0;
        m.PlayingChanged += (_, _) => events++;
        m.Set("long", Constant(0.01f, Sr * 20));
        m.Set("short", Constant(0.01f, Block * 2));
        var block = new float[Block];
        // warm up: start, end, stop, drain
        for (int i = 0; i < 20; i++)
        {
            m.Toggle("long", 1); m.Toggle("short", 1);
            for (int k = 0; k < 4; k++) m.MixInto(block);
            m.StopAll();
            m.MixInto(block);
        }
        long allocated = AllocatedBytes(() =>
        {
            long total = 0;
            for (int i = 0; i < 200; i++)
            {
                m.Toggle("long", 1); m.Toggle("short", 1); // commands are enqueued on the "UI" side, unmeasured
                long before = GC.GetAllocatedBytesForCurrentThread();
                m.MixInto(block);  // starts both
                m.MixInto(block);
                m.MixInto(block);  // "short" ends
                total += GC.GetAllocatedBytesForCurrentThread() - before;
                m.StopAll();
                before = GC.GetAllocatedBytesForCurrentThread();
                m.MixInto(block);  // stops "long"
                total += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            return total;
        });
        Assert.Equal(0, allocated);
        Assert.True(events > 0);
    }

    // ---------- starter sounds ----------

    [Fact]
    public void StarterSynth_WritesEveryStarterSound()
    {
        string folder = Path_("starter");
        StarterSoundSynth.WriteAll(folder);
        foreach (var item in StarterSounds.Items())
        {
            string file = Path.Combine(folder, item.FileName);
            Assert.True(File.Exists(file), item.Id + " missing");
            var x = SoundLoader.Load(file);
            double seconds = x.Length / (double)Sr;
            Assert.InRange(seconds, 0.3, 6);
            Assert.InRange(Signals.Peak(x), 0.3, 0.9);
            Assert.True(Signals.AllFinite(x));
            Assert.InRange(Math.Abs(Mean(x)), 0, 0.01); // no DC offset
            Assert.True(Math.Abs(x[0]) < 0.05, item.Id + " starts with a click");
            Assert.True(Math.Abs(x[^1]) < 0.01, item.Id + " ends with a click");
        }
    }

    [Fact]
    public void StarterSynth_SkipsFilesThatAlreadyExist()
    {
        string folder = Path_("starter2");
        Directory.CreateDirectory(folder);
        string mine = Path.Combine(folder, "ding.wav");
        File.WriteAllText(mine, "mine");
        StarterSoundSynth.WriteAll(folder);
        Assert.Equal("mine", File.ReadAllText(mine));
        Assert.True(File.Exists(Path.Combine(folder, "airhorn.wav")));
    }

    [Fact]
    public void StarterSynth_IsDeterministic()
    {
        string a = Path_("det_a"), b = Path_("det_b");
        StarterSoundSynth.WriteAll(a);
        StarterSoundSynth.WriteAll(b);
        foreach (var item in StarterSounds.Items())
            Assert.Equal(File.ReadAllBytes(Path.Combine(a, item.FileName)), File.ReadAllBytes(Path.Combine(b, item.FileName)));
    }

    [Fact]
    public void StarterSynth_Ding_HasItsPitch()
    {
        string folder = Path_("starter3");
        StarterSoundSynth.WriteAll(folder);
        var x = SoundLoader.Load(Path.Combine(folder, "ding.wav"));
        var head = Signals.Slice(x, 0.02, 0.3);
        Assert.True(Signals.Magnitude(head, 1320) > 3 * Signals.Magnitude(head, 900));
    }

    private static double Mean(float[] x)
    {
        double s = 0;
        foreach (float v in x) s += v;
        return s / x.Length;
    }
}
