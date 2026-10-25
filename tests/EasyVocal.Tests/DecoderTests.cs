using System;
using System.IO;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Sounds;
using EasyVocal.Tests.Helpers;
using Xunit.Abstractions;

namespace EasyVocal.Tests;

/// <summary>
/// The portable decoders (what macOS and Linux use): MP3 through NLayer, OGG through NVorbis, WAV/AIFF, and the ffmpeg
/// fallback (run for real only when ffmpeg is installed; never downloaded). The fixtures are 1 s of a 440 Hz tone at
/// half scale, 44.1 kHz (the OGG one is 0.92 s: its encoder drops the last partial blocks).
/// </summary>
public class DecoderTests : IDisposable
{
    private const int Sr = AudioFormat.SampleRate;
    private readonly ITestOutputHelper output;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "EV_decoders_" + Guid.NewGuid().ToString("N"));

    public DecoderTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private string P(string name) => Path.Combine(dir, name);

    private static void AssertTone(float[] x, int lengthSlack)
    {
        Assert.InRange(x.Length, Sr - lengthSlack, Sr + lengthSlack);
        Assert.InRange(Signals.EstimatePitch(x), 430, 450);
        Assert.InRange(Signals.Peak(x), 0.4, 0.62);
    }

    [Fact]
    public void Mp3_DecodesThroughNLayer_ToMono48k()
    {
        var x = new Mp3Decoder().Decode(Fixture("tone440_mono_44k.mp3"), default);
        AssertTone(x, 4000); // MP3 adds encoder padding
    }

    [Fact]
    public void Ogg_DecodesThroughNVorbis_StereoToMono48k()
    {
        var x = new OggDecoder().Decode(Fixture("tone440_stereo_44k.ogg"), default);
        AssertTone(x, 4500);
    }

    [Fact]
    public void PortableList_ReadsMp3AndOgg_ThroughTheLoader()
    {
        AssertTone(SoundLoader.Load(Fixture("tone440_mono_44k.mp3"), SoundDecoders.Portable()), 4000);
        AssertTone(SoundLoader.Load(Fixture("tone440_stereo_44k.ogg"), SoundDecoders.Portable()), 4500);
    }

    [Fact]
    public void PortableList_IsWavOggMp3ThenFfmpeg()
    {
        var list = SoundDecoders.Portable();
        Assert.Collection(list,
            d => Assert.IsType<WavAiffDecoder>(d),
            d => Assert.IsType<OggDecoder>(d),
            d => Assert.IsType<Mp3Decoder>(d),
            d => Assert.IsType<FfmpegDecoder>(d));
        Assert.True(list[0].CanDecode(".wav") && list[0].CanDecode(".aif") && !list[0].CanDecode(".mp3"));
        Assert.True(list[1].CanDecode(".ogg") && !list[1].CanDecode(".wav"));
        Assert.True(list[2].CanDecode(".mp3") && !list[2].CanDecode(".ogg"));
    }

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".ogg")]
    [InlineData(".wav")]
    public void DamagedFile_IsAFriendlyError_AndLeftFreeToDelete(string ext)
    {
        string path = P("bad" + ext);
        var bytes = new byte[8000];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        var noFfmpeg = new ISoundDecoder[] { new WavAiffDecoder(), new OggDecoder(), new Mp3Decoder(), new FfmpegDecoder(null) };
        Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path, noFfmpeg));
        File.Delete(path); // throws if a decoder left the file open
    }

    [Fact]
    public void TruncatedMp3_StillGivesWhatIsThere()
    {
        var full = File.ReadAllBytes(Fixture("tone440_mono_44k.mp3"));
        string path = P("half.mp3");
        File.WriteAllBytes(path, full[..(full.Length / 2)]);
        var x = SoundLoader.Load(path, SoundDecoders.Portable());
        Assert.InRange(x.Length, Sr / 4, Sr * 3 / 4);
        Assert.InRange(Signals.EstimatePitch(x), 430, 450);
    }

    [Fact]
    public void CompressedWav_IsNotTheManagedReadersJob()
    {
        // A WAV header saying "ADPCM" (format 2): the managed reader refuses it, so another decoder (Media Foundation
        // on Windows, ffmpeg elsewhere) gets the file; with none left, it is a friendly "damaged".
        string path = P("adpcm.wav");
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write("RIFF"u8); w.Write(36 + 100); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)2); w.Write((short)1); w.Write(8000); w.Write(4000); w.Write((short)256); w.Write((short)4);
            w.Write("data"u8); w.Write(100); w.Write(new byte[100]);
        }
        Assert.ThrowsAny<Exception>(() => new WavAiffDecoder().Decode(path, default));
        var ex = Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path, new ISoundDecoder[] { new WavAiffDecoder() }));
        Assert.Equal(SoundLoader.Damaged, ex.Message);
    }

    // ---------- WAVE_FORMAT_EXTENSIBLE ----------

    private static readonly Guid PcmGuid = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatGuid = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>0.5 s of a 440 Hz tone at half scale in every channel, as a WAVE_FORMAT_EXTENSIBLE file.</summary>
    private string ExtensibleWav(string name, int rate, int bits, int channels, Guid subFormat)
    {
        int frames = rate / 2, block = channels * bits / 8;
        var data = new byte[frames * block];
        for (int n = 0; n < frames; n++)
        {
            double v = 0.5 * Math.Sin(2 * Math.PI * 440 * n / rate);
            for (int c = 0; c < channels; c++)
            {
                int at = n * block + c * bits / 8;
                if (subFormat == FloatGuid) BitConverter.TryWriteBytes(data.AsSpan(at, 4), (float)v);
                else if (bits == 24)
                {
                    int s = (int)Math.Round(v * 8388607);
                    data[at] = (byte)s; data[at + 1] = (byte)(s >> 8); data[at + 2] = (byte)(s >> 16);
                }
                else BitConverter.TryWriteBytes(data.AsSpan(at, 2), (short)Math.Round(v * 32767));
            }
        }
        string path = P(name);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(4 + 8 + 40 + 8 + data.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(40);
        w.Write((short)-2); // WAVE_FORMAT_EXTENSIBLE (0xFFFE)
        w.Write((short)channels); w.Write(rate); w.Write(rate * block); w.Write((short)block); w.Write((short)bits);
        w.Write((short)22); w.Write((short)bits); w.Write(channels == 6 ? 0x3F : 0x3); // valid bits, channel mask
        w.Write(subFormat.ToByteArray());
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        return path;
    }

    [Theory]
    [InlineData(44100, 24, 2, "pcm")]
    [InlineData(48000, 32, 2, "float")]
    [InlineData(48000, 16, 6, "pcm")]   // 5.1
    [InlineData(96000, 24, 6, "pcm")]
    public void ExtensibleWav_IsReadByTheManagedReader(int rate, int bits, int channels, string kind)
    {
        var path = ExtensibleWav($"ext_{rate}_{bits}_{channels}.wav", rate, bits, channels, kind == "float" ? FloatGuid : PcmGuid);
        var (x, decoder) = SoundLoader.LoadWith(path, new ISoundDecoder[] { new WavAiffDecoder() });
        Assert.IsType<WavAiffDecoder>(decoder);
        Assert.InRange(x.Length, Sr / 2 - 50, Sr / 2 + 50);
        Assert.InRange(Signals.EstimatePitch(x), 435, 445);
        Assert.InRange(Signals.Peak(x), 0.48, 0.52); // every channel the same: the average keeps the level
    }

    // ---------- the loader keeps looking ----------

    private sealed class Scripted(string name, Func<float[]> result) : ISoundDecoder
    {
        public int Calls;
        public string Name => name;
        public bool CanDecode(string extension) => true;
        public float[] Decode(string path, CancellationToken ct)
        {
            Calls++;
            return result();
        }
    }

    [Fact]
    public void Loader_EmptyOrTooLongFromOneDecoder_TriesTheNext()
    {
        var path = Instruction("any.wav", "x");
        var empty = new Scripted("empty", () => Array.Empty<float>());
        var tooLong = new Scripted("too long", () => throw SoundLoader.TooLong());
        var good = new Scripted("good", () => new float[] { 0.1f, float.NaN, 0.3f });
        var (x, decoder) = SoundLoader.LoadWith(path, new ISoundDecoder[] { empty, tooLong, good });
        Assert.Same(good, decoder);
        Assert.Equal(new[] { 0.1f, 0f, 0.3f }, x); // NaN cleaned
        Assert.Equal(1, empty.Calls);
        Assert.Equal(1, tooLong.Calls);
    }

    [Fact]
    public void Loader_WhenNoDecoderGaveASound_TheFirstFriendlyAnswerWins_ElseDamaged()
    {
        var path = Instruction("any2.wav", "x");
        var empty = new Scripted("empty", () => Array.Empty<float>());
        var broken = new Scripted("broken", () => throw new InvalidDataException("nope"));
        var tooLong = new Scripted("too long", () => throw SoundLoader.TooLong());
        Assert.Equal(SoundLoader.Empty().Message,
            Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path, new ISoundDecoder[] { broken, empty, tooLong })).Message);
        Assert.Equal(SoundLoader.TooLong().Message,
            Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path, new ISoundDecoder[] { tooLong, empty })).Message);
        var damaged = Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path, new ISoundDecoder[] { broken }));
        Assert.Equal(SoundLoader.Damaged, damaged.Message);
        Assert.IsType<InvalidDataException>(damaged.InnerException);
    }

    [Fact]
    public void Loader_Cancelled_Stops()
    {
        var path = Instruction("any3.wav", "x");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var never = new Scripted("never", () => new float[] { 1 });
        Assert.ThrowsAny<OperationCanceledException>(() => SoundLoader.Load(path, new ISoundDecoder[] { never }, cancel.Token));
        Assert.Equal(0, never.Calls);
        // A managed decoder checks between chunks too.
        var wav = P("long.wav");
        SoundWriter.WriteWav(wav, Signals.Sine(440, 5));
        Assert.ThrowsAny<OperationCanceledException>(() => new WavAiffDecoder().Decode(wav, cancel.Token));
    }

    // ---------- ffmpeg ----------

    [Fact]
    public void Ffmpeg_Missing_TakesNothing_AndTheLoaderSaysToInstallIt()
    {
        var none = new FfmpegDecoder(null);
        Assert.False(none.Available);
        Assert.False(none.CanDecode(".flac"));
        Assert.False(new FfmpegDecoder(P("no-such-ffmpeg")).Available);

        string flac = P("tone.flac");
        MiniFlac.Write(flac, Signals.Sine(440, 0.2));
        var ex = Assert.Throws<SoundLoadException>(() => SoundLoader.Load(flac, new ISoundDecoder[] { new WavAiffDecoder(), none }));
        Assert.Equal(SoundLoader.NeedsFfmpeg, ex.Message);
        Assert.Contains("ffmpeg", ex.Message);
    }

    [Fact]
    public void Ffmpeg_IsFoundOnThePath()
    {
        string bin = P("bin");
        Directory.CreateDirectory(bin);
        string exe = Path.Combine(bin, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        File.WriteAllText(exe, "");
        Assert.Equal(exe, FfmpegDecoder.Find(P("nothing-here") + Path.PathSeparator + "\"" + bin + "\""));
        if (OperatingSystem.IsWindows()) Assert.Null(FfmpegDecoder.Find(P("nothing-here")));
        Assert.Equal(exe, new FfmpegDecoder(exe).ProgramPath);
    }

    [Fact]
    public void Ffmpeg_AsksForMono48kFloat_OnStdout_FirstSoundTrackOnly()
    {
        var a = string.Join(" ", FfmpegDecoder.Arguments("in.mp4"));
        Assert.Contains("-nostdin", a);
        Assert.Contains("-i file:" + Path.GetFullPath("in.mp4"), a); // a name is never read as a URL or protocol
        Assert.Contains("-map 0:a:0", a);
        Assert.Contains("-vn", a);
        Assert.Contains("-ac 1", a);
        Assert.Contains("-ar 48000", a);
        Assert.Contains("-f f32le pipe:1", a);
        Assert.Contains("-t 601", a); // a little over the 10-minute limit, so "too long" is still noticed
    }

    [Fact]
    public void Ffmpeg_Output_IsReadAsLittleEndianFloats_UpToTheLimit()
    {
        var expected = new[] { 0f, 0.5f, -1f, 0.25f };
        var bytes = new byte[expected.Length * 4 + 3]; // a cut-off last sample is dropped
        for (int i = 0; i < expected.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), expected[i]);
        Assert.Equal(expected, FfmpegDecoder.ReadFloats(new MemoryStream(bytes), out bool tooLong));
        Assert.False(tooLong);

        long tooMany = ((long)(SoundLoader.MaxSeconds * Sr) + Sr) * 4 + 4;
        FfmpegDecoder.ReadFloats(new MemoryStream(new byte[tooMany]), out tooLong);
        Assert.True(tooLong);
    }

    // ---------- ffmpeg, played by a stand-in program (tests/EasyVocal.FfmpegStandIn) ----------

    /// <summary>The stand-in built next to these tests (same configuration), or null when it isn't built.</summary>
    private static string? StandIn()
    {
        var tfmDir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string tfm = tfmDir.Name, config = tfmDir.Parent?.Name ?? "Debug";
        for (var dir = tfmDir; dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "EasyVocal.sln"))) continue;
            var exe = Path.Combine(dir.FullName, "tests", "EasyVocal.FfmpegStandIn", "bin", config, tfm,
                OperatingSystem.IsWindows() ? "EasyVocal.FfmpegStandIn.exe" : "EasyVocal.FfmpegStandIn");
            return File.Exists(exe) ? exe : null;
        }
        return null;
    }

    /// <summary>A "sound file" whose text tells the stand-in what to do.</summary>
    private string Instruction(string name, string text)
    {
        var path = P(name);
        File.WriteAllText(path, text);
        return path;
    }

    private FfmpegDecoder? StandInDecoder(TimeSpan? timeout = null)
    {
        var exe = StandIn();
        if (exe == null) output.WriteLine("SKIPPED: the ffmpeg stand-in isn't built (build the solution).");
        return exe == null ? null : new FfmpegDecoder(exe, timeout);
    }

    [Fact]
    public void Ffmpeg_StandIn_DecodesWhatItWrites_AndGetsTheFileAsFileColonPath()
    {
        if (StandInDecoder() is not { } ffmpeg) return;
        var x = SoundLoader.Load(Instruction("tone.m4a", "tone 1"), new ISoundDecoder[] { ffmpeg });
        Assert.Equal(Sr, x.Length);
        Assert.InRange(Signals.EstimatePitch(x), 435, 445);
        Assert.InRange(Signals.Peak(x), 0.49, 0.51);
    }

    [Fact]
    public void Ffmpeg_NonZeroExitAfterSomeOutput_IsAFailure()
    {
        if (StandInDecoder() is not { } ffmpeg) return;
        var path = Instruction("partial.m4a", "partial-fail");
        Assert.Throws<InvalidDataException>(() => ffmpeg.Decode(path, default));
        Assert.Equal(SoundLoader.Damaged, Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path, new ISoundDecoder[] { ffmpeg })).Message);
    }

    [Fact]
    public void Ffmpeg_PastItsDeadline_IsStopped_AndSaysItTookTooLong()
    {
        if (StandInDecoder(TimeSpan.FromSeconds(1.5)) is not { } ffmpeg) return;
        var path = Instruction("hang.m4a", "hang");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<SoundLoadException>(() => ffmpeg.Decode(path, default));
        Assert.Equal(SoundLoader.TookTooLong, ex.Message);
        Assert.InRange(watch.Elapsed.TotalSeconds, 1, 10); // the deadline, plus at most the short wait after the kill
        AssertGone(path);
    }

    [Fact]
    public void Ffmpeg_Cancelled_IsStoppedRightAway()
    {
        if (StandInDecoder(TimeSpan.FromSeconds(60)) is not { } ffmpeg) return;
        var path = Instruction("cancel.m4a", "hang");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => SoundLoader.Load(path, new ISoundDecoder[] { ffmpeg }, cancel.Token));
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.5, 10);
        AssertGone(path);
    }

    /// <summary>The stand-in that wrote its process id next to <paramref name="input"/> no longer runs.</summary>
    private static void AssertGone(string input)
    {
        // Stopped before it even wrote its id, or while writing it (a slow start on a busy computer): nothing of it runs.
        if (!File.Exists(input + ".pid") || !int.TryParse(File.ReadAllText(input + ".pid"), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int pid)) return;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            Assert.True(p.WaitForExit(5000), "the stand-in was stopped");
        }
        catch (ArgumentException) { /* already gone */ }
    }

    [Fact]
    public void Ffmpeg_EmptyOutput_LetsTheLoaderSayEmpty_OnlyWhenNothingElseWorked()
    {
        if (StandInDecoder() is not { } ffmpeg) return;
        var empty = Instruction("empty.m4a", "empty");
        Assert.Equal(SoundLoader.Empty().Message, Assert.Throws<SoundLoadException>(() => SoundLoader.Load(empty, new ISoundDecoder[] { ffmpeg })).Message);
    }

    [Fact]
    public void Ffmpeg_WhenInstalled_DecodesToMono48k()
    {
        var ffmpeg = new FfmpegDecoder();
        if (!ffmpeg.Available)
        {
            output.WriteLine("SKIPPED: ffmpeg is not installed on this computer (it is never downloaded by the tests).");
            return;
        }
        output.WriteLine("ffmpeg: " + ffmpeg.ProgramPath);
        var only = new ISoundDecoder[] { ffmpeg };
        AssertTone(SoundLoader.Load(Fixture("tone440_stereo_44k.ogg"), only), 4500);
        AssertTone(SoundLoader.Load(Fixture("tone440_mono_44k.mp3"), only), 4000);

        string flac = P("tone.flac");
        var tone = new float[44100];
        for (int n = 0; n < tone.Length; n++) tone[n] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * n / 44100.0));
        MiniFlac.Write(flac, tone);
        AssertTone(SoundLoader.Load(flac, only), 200);

        string bad = P("bad.m4a");
        File.WriteAllText(bad, "not a sound");
        Assert.Equal(SoundLoader.Damaged, Assert.Throws<SoundLoadException>(() => SoundLoader.Load(bad, only)).Message);
    }
}
