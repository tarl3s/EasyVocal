using System;
using System.IO;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Sounds;
using EasyVocal.Tests.Helpers;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace EasyVocal.Tests;

/// <summary>What Windows decodes and encodes (Media Foundation): MP3 written by Windows, FLAC, M4A, WMA, a video's
/// sound, and MP3 export. The loader uses the Windows decoders here (<see cref="WindowsCodecs"/>).</summary>
public class WindowsSoundTests : IDisposable
{
    private const int Sr = AudioFormat.SampleRate;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EV_winformats_" + Guid.NewGuid().ToString("N"));

    public WindowsSoundTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string P(string name) => Path.Combine(_dir, name);
    private string Path_(string name) => P(name);

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
    public void Load_AFileThatIsNoVideo_IsAFriendlyError()
    {
        string path = P("clip.mp4");
        File.WriteAllText(path, "not a video");
        var ex = Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path));
        Assert.Equal("We couldn't read that sound file. It may be damaged.", ex.Message);
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

    [Fact]
    public void Mp3FromWindows_DecodesTheSameThroughNLayer()
    {
        string wav = P("src.wav"), mp3 = P("same.mp3");
        SoundWriter.WriteWav(wav, Signals.Sine(440, 1));
        if (!SoundWriter.Mp3Supported)
        {
            Console.WriteLine("Skipped: no MP3 encoder on this machine");
            return;
        }
        SoundWriter.WriteMp3(mp3, SoundLoader.Load(wav));
        var windows = SoundLoader.Load(mp3);
        var portable = SoundLoader.Load(mp3, SoundDecoders.Portable());
        Assert.InRange(portable.Length, windows.Length - 4000, windows.Length + 4000); // decoders trim padding differently
        Assert.InRange(Signals.EstimatePitch(portable), 430, 450);
        Assert.InRange(Signals.Peak(portable), Signals.Peak(windows) * 0.8, Signals.Peak(windows) * 1.2);
    }

    [Fact]
    public void WindowsDecoderOrder_ManagedReadersFirst_ThenMediaFoundation()
    {
        var names = EasyVocal.Platform.Windows.WindowsPlatform.Decoders().Select(d => d.Name).ToList();
        Assert.Equal(new[] { "WAV/AIFF", "OGG (NVorbis)", "Media Foundation", "MP3 (NLayer)", "ffmpeg" }, names);
        Assert.Same(SoundDecoders.Current[2].GetType(), typeof(EasyVocal.Platform.Windows.MediaFoundationDecoder)); // set by WindowsCodecs
    }
}
