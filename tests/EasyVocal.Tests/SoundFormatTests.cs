using System;
using System.IO;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Sounds;
using EasyVocal.Tests.Helpers;
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
        var patterns = SoundLoader.PickerPatterns(SoundLoader.Extensions);
        foreach (var ext in new[] { ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".mp4", ".m4v", ".mov", ".wmv" })
        {
            Assert.Contains(ext, SoundLoader.Extensions);
            Assert.True(SoundLoader.IsSupported("x" + ext.ToUpperInvariant()));
            Assert.Contains("*" + ext, patterns);
            Assert.Contains("*" + ext.ToUpperInvariant(), patterns); // Linux pickers match case-sensitively
        }
        Assert.False(SoundLoader.IsSupported("notes.txt"));
        Assert.False(SoundLoader.IsSupported(null));
        Assert.All(patterns, p => Assert.DoesNotContain(" ", p));
        Assert.Contains("audio/mpeg", SoundLoader.MimeTypes);
        Assert.Contains("video/mp4", SoundLoader.MimeTypes);
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

}
