using System;
using System.IO;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Sounds;
using EasyVocal.Tests.Helpers;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace EasyVocal.Tests;

/// <summary>The extra input formats (decoded by Windows) and the files the sound editor writes.</summary>
public class SoundFormatTests : IDisposable
{
    private const int Sr = AudioFormat.SampleRate;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EV_formats_" + Guid.NewGuid().ToString("N"));

    public SoundFormatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Extensions_CoverSoundAndVideoFiles()
    {
        foreach (var ext in new[] { ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".mp4", ".m4v", ".mov", ".wmv" })
        {
            Assert.Contains(ext, SoundLoader.Extensions);
            Assert.True(SoundLoader.IsSupported("x" + ext.ToUpperInvariant()));
            Assert.Contains("*" + ext, SoundLoader.DialogPattern);
        }
        Assert.False(SoundLoader.IsSupported("notes.txt"));
        Assert.False(SoundLoader.IsSupported(null));
        Assert.DoesNotContain(" ", SoundLoader.DialogPattern);
    }

    /// <summary>A 440 Hz tone as a 44.1 kHz 16-bit WAV (what an encoder is fed).</summary>
    private string ToneWav(int channels = 1)
    {
        string wav = P("src_" + channels + ".wav");
        using var w = new WaveFileWriter(wav, new WaveFormat(44100, 16, channels));
        var frame = new float[channels];
        for (int n = 0; n < 44100; n++)
        {
            Array.Fill(frame, (float)(0.5 * Math.Sin(2 * Math.PI * 440 * n / 44100.0)));
            w.WriteSamples(frame, 0, channels);
        }
        return wav;
    }

    /// <summary>Encodes with one of Windows' encoders; false (the test is skipped) when this PC has none.</summary>
    private static bool TryEncode(string wav, string target, Guid subtype, int bitrate)
    {
        try
        {
            MediaFoundationApi.Startup();
            using var reader = new WaveFileReader(wav);
            var type = MediaFoundationEncoder.SelectMediaType(subtype, reader.WaveFormat, bitrate);
            if (type == null)
            {
                Console.WriteLine("Skipped: no encoder for " + Path.GetExtension(target));
                return false;
            }
            using var encoder = new MediaFoundationEncoder(type);
            encoder.Encode(target, reader);
            return File.Exists(target) && new FileInfo(target).Length > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Skipped: encoding " + Path.GetExtension(target) + " failed (" + ex.Message + ")");
            return false;
        }
    }

    [Theory]
    [InlineData(".m4a", 1)]
    [InlineData(".m4a", 2)]
    [InlineData(".wma", 2)]
    [InlineData(".mp4", 2)] // the sound track of an MP4 (here one with no picture)
    public void Load_FilesWindowsDecodes_GiveMono48k(string ext, int channels)
    {
        Guid subtype = ext switch
        {
            ".m4a" or ".mp4" => AudioSubtypes.MFAudioFormat_AAC,
            _ => AudioSubtypes.MFAudioFormat_WMAudioV8,
        };
        string target = P("tone" + channels + ext);
        if (!TryEncode(ToneWav(channels), target, subtype, 96000)) return;

        var x = SoundLoader.Load(target);
        Assert.InRange(x.Length, Sr - 6000, Sr + 6000); // encoders add a little padding
        Assert.InRange(Signals.EstimatePitch(x), 430, 450);
        Assert.InRange(Signals.Peak(x), 0.35, 0.7);
    }

    [Fact]
    public void Load_Flac_GivesMono48k()
    {
        var tone = new float[44100];
        for (int n = 0; n < tone.Length; n++) tone[n] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * n / 44100.0));
        string path = P("tone.flac");
        MiniFlac.Write(path, tone);
        float[] x;
        try { x = SoundLoader.Load(path); }
        catch (SoundLoadException ex) when (ex.InnerException is System.Runtime.InteropServices.COMException)
        {
            Console.WriteLine("Skipped: this Windows has no FLAC decoder (" + ex.InnerException.Message + ")");
            return;
        }
        Assert.InRange(x.Length, Sr - 100, Sr + 100);
        Assert.InRange(Signals.EstimatePitch(x), 435, 445);
        Assert.InRange(Signals.Peak(x), 0.45, 0.55);
    }

    [Fact]
    public void Load_Aiff_GivesMono48k()
    {
        string path = P("tone.aiff");
        using (var w = new AiffFileWriter(path, new WaveFormat(44100, 16, 2)))
        {
            var frame = new float[2];
            for (int n = 0; n < 44100; n++)
            {
                frame[0] = frame[1] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * n / 44100.0));
                w.WriteSamples(frame, 0, 2);
            }
        }
        var x = SoundLoader.Load(path);
        Assert.InRange(x.Length, Sr - 10, Sr + 10);
        Assert.InRange(Signals.EstimatePitch(x), 435, 445);
    }

    [Theory]
    [InlineData(".aiff")]
    [InlineData(".wav")]
    [InlineData(".mp3")]
    [InlineData(".ogg")]
    [InlineData(".m4a")]
    [InlineData(".flac")]
    public void Load_ADamagedFile_LeavesItFreeToDelete(string ext)
    {
        string path = P("bad" + ext);
        var bytes = new byte[5000];
        new Random(3).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path));
        File.Delete(path); // throws if the file was left open
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Load_AFileThatIsNoVideo_IsAFriendlyError()
    {
        string path = P("clip.mp4");
        File.WriteAllText(path, "not a video");
        var ex = Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path));
        Assert.Equal("We couldn't read that sound file. It may be damaged.", ex.Message);
    }

    [Fact]
    public void UnsupportedExtension_SaysWhichFilesWork()
    {
        string path = P("doc.pdf");
        File.WriteAllText(path, "x");
        var ex = Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path));
        Assert.Contains("FLAC", ex.Message);
    }

    // ---------- writing ----------

    [Fact]
    public void WriteWav_RoundTripsAndClips()
    {
        var x = Signals.Sine(300, 0.5, 0.4f);
        x[10] = 3f;
        x[11] = float.NaN;
        string path = P("out.wav");
        SoundWriter.WriteWav(path, x);
        var y = SoundLoader.Load(path);
        Assert.Equal(x.Length, y.Length);
        Assert.Equal(1f, y[10], 3);
        Assert.Equal(0f, y[11]);
        Assert.Equal(x[1000], y[1000], 3);
    }

    [Fact]
    public void WriteMp3_GivesAPlayableFile_WhenWindowsHasTheEncoder()
    {
        if (!SoundWriter.Mp3Supported)
        {
            Console.WriteLine("Skipped: no MP3 encoder on this machine");
            return;
        }
        string path = P("out.mp3");
        SoundWriter.WriteMp3(path, Signals.Sine(440, 1));
        var y = SoundLoader.Load(path);
        Assert.InRange(y.Length, Sr - 4000, Sr + 4000);
        Assert.InRange(Signals.EstimatePitch(y), 430, 450);
    }
}
