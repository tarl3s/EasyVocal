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
        var x = new Mp3Decoder().Decode(Fixture("tone440_mono_44k.mp3"));
        AssertTone(x, 4000); // MP3 adds encoder padding
    }

    [Fact]
    public void Ogg_DecodesThroughNVorbis_StereoToMono48k()
    {
        var x = new OggDecoder().Decode(Fixture("tone440_stereo_44k.ogg"));
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
        Assert.ThrowsAny<Exception>(() => new WavAiffDecoder().Decode(path));
        var ex = Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path, new ISoundDecoder[] { new WavAiffDecoder() }));
        Assert.Equal(SoundLoader.Damaged, ex.Message);
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
        Assert.Contains("-i in.mp4", a);
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

    [Fact]
    public void Ffmpeg_AProgramThatFails_IsAFriendlyError()
    {
        // Any program that doesn't understand ffmpeg's arguments and exits with an error: the dotnet host does.
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(dotnet) || !File.Exists(dotnet))
        {
            output.WriteLine("SKIPPED: DOTNET_HOST_PATH is not set, so there is no harmless program to stand in for a broken ffmpeg.");
            return;
        }
        string flac = P("tone.flac");
        MiniFlac.Write(flac, Signals.Sine(440, 0.2));
        var broken = new FfmpegDecoder(dotnet, TimeSpan.FromSeconds(20));
        Assert.ThrowsAny<Exception>(() => broken.Decode(flac));
        var ex = Assert.Throws<SoundLoadException>(() => SoundLoader.Load(flac, new ISoundDecoder[] { broken }));
        Assert.Equal(SoundLoader.Damaged, ex.Message);
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
