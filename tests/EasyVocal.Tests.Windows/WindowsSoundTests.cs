using System;
using System.IO;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Sounds;
using EasyVocal.Tests.Helpers;
using NAudio.MediaFoundation;
using EasyVocal.Platform.Windows;
using NAudio.Wave;

namespace EasyVocal.Tests;

/// <summary>What Windows decodes and encodes (Media Foundation): MP3 written by Windows, FLAC, M4A, WMA, a video's
/// sound, compressed WAVs (ACM), and MP3 export. The loader gets Windows' decoder list (<see cref="WindowsPlatform.Decoders"/>).</summary>
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

    private static readonly IReadOnlyList<ISoundDecoder> Win = WindowsPlatform.Decoders();
    private static readonly MediaFoundationMp3Encoder Mp3 = new();

    private static float[] Load(string path) => SoundLoader.Load(path, Win);
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
        var x = Load(mp3);
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

        var x = Load(target);
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
        try { x = Load(path); }
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
        var ex = Assert.Throws<SoundLoadException>(() => Load(path));
        Assert.Equal("We couldn't read that sound file. It may be damaged.", ex.Message);
    }

    [Fact]
    public void WriteMp3_GivesAPlayableFile_WhenWindowsHasTheEncoder()
    {
        if (!Mp3.Supported)
        {
            Console.WriteLine("Skipped: no MP3 encoder on this machine");
            return;
        }
        string path = P("out.mp3");
        Mp3.Write(path, Signals.Sine(440, 1));
        var y = Load(path);
        Assert.InRange(y.Length, Sr - 4000, Sr + 4000);
        Assert.InRange(Signals.EstimatePitch(y), 430, 450);
    }

    [Fact]
    public void Mp3FromWindows_DecodesTheSameThroughNLayer()
    {
        string wav = P("src.wav"), mp3 = P("same.mp3");
        SoundWriter.WriteWav(wav, Signals.Sine(440, 1));
        if (!Mp3.Supported)
        {
            Console.WriteLine("Skipped: no MP3 encoder on this machine");
            return;
        }
        Mp3.Write(mp3, Load(wav));
        var windows = Load(mp3);
        var portable = SoundLoader.Load(mp3, SoundDecoders.Portable());
        Assert.InRange(portable.Length, windows.Length - 4000, windows.Length + 4000); // decoders trim padding differently
        Assert.InRange(Signals.EstimatePitch(portable), 430, 450);
        Assert.InRange(Signals.Peak(portable), Signals.Peak(windows) * 0.8, Signals.Peak(windows) * 1.2);
    }

    [Fact]
    public void WindowsDecoderOrder_IsWhat02Used_ThenSecondChances()
    {
        var names = Win.Select(d => d.Name).ToList();
        Assert.Equal(new[] { "WAV/AIFF", "WAV (Windows ACM)", "OGG (NVorbis)", "Media Foundation", "MP3 (Windows ACM)", "MP3 (NLayer)", "ffmpeg" }, names);
        Assert.Equal(names, WindowsPlatform.Services.Decoders.Select(d => d.Name));
    }

    /// <summary>1 s of a 440 Hz tone, 8 kHz 16-bit mono, converted by Windows' ACM into <paramref name="format"/>.</summary>
    private string CompressedWav(string name, WaveFormat format)
    {
        var pcm = new WaveFormat(8000, 16, 1);
        var bytes = new byte[8000 * 2];
        for (int n = 0; n < 8000; n++)
        {
            short s = (short)(0.5 * 32767 * Math.Sin(2 * Math.PI * 440 * n / 8000.0));
            bytes[2 * n] = (byte)s;
            bytes[2 * n + 1] = (byte)(s >> 8);
        }
        string path = P(name);
        using var source = new RawSourceWaveStream(new MemoryStream(bytes), pcm);
        using var converted = new WaveFormatConversionStream(format, source);
        WaveFileWriter.CreateWaveFile(path, converted);
        return path;
    }

    private static readonly int[] ImaIndex = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
    private static readonly int[] ImaStep =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
        1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
        7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };

    /// <summary>16 blocks (8080 samples, about 1 s) of a 440 Hz tone at 8 kHz as an IMA ADPCM WAV (256-byte mono blocks).</summary>
    private string ImaAdpcmWav(string name)
    {
        const int perBlock = 505, blockBytes = 256;
        var data = new byte[16 * blockBytes];
        for (int b = 0; b < 16; b++)
        {
            short Sample(int k) => (short)(0.5 * 32767 * Math.Sin(2 * Math.PI * 440 * (b * perBlock + k) / 8000.0));
            int at = b * blockBytes, predictor = Sample(0), index = 0;
            data[at] = (byte)predictor; data[at + 1] = (byte)(predictor >> 8); data[at + 2] = (byte)index; data[at + 3] = 0;
            for (int k = 1; k < perBlock; k++)
            {
                int diff = Sample(k) - predictor, nibble = 0, step = ImaStep[index], delta = step >> 3;
                if (diff < 0) { nibble = 8; diff = -diff; }
                if (diff >= step) { nibble |= 4; diff -= step; delta += step; }
                step >>= 1;
                if (diff >= step) { nibble |= 2; diff -= step; delta += step; }
                step >>= 1;
                if (diff >= step) { nibble |= 1; delta += step; }
                predictor = Math.Clamp(predictor + ((nibble & 8) != 0 ? -delta : delta), -32768, 32767);
                index = Math.Clamp(index + ImaIndex[nibble], 0, 88);
                int pos = at + 4 + (k - 1) / 2;
                data[pos] |= (byte)((k - 1) % 2 == 0 ? nibble : nibble << 4);
            }
        }
        string path = P(name);
        using (var w = new BinaryWriter(File.Create(path)))
        {
            // RIFF/WAVE with fmt (WAVE_FORMAT_IMA_ADPCM, cbSize 2: samples per block), fact (sample count) and data.
            w.Write("RIFF"u8); w.Write(4 + 8 + 20 + 8 + 4 + 8 + data.Length); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(20);
            w.Write((short)0x11); w.Write((short)1); w.Write(8000); w.Write(8000 * blockBytes / perBlock);
            w.Write((short)blockBytes); w.Write((short)4); w.Write((short)2); w.Write((short)perBlock);
            w.Write("fact"u8); w.Write(4); w.Write(16 * perBlock);
            w.Write("data"u8); w.Write(data.Length); w.Write(data);
        }
        return path;
    }

    [Theory]
    [InlineData("mulaw")]
    [InlineData("ima-adpcm")]
    public void CompressedWav_IsReadThroughAcm_LikeIn02(string kind)
    {
        // mu-law is written by Windows' own ACM encoder; IMA ADPCM by a small encoder here (ACM has no IMA encoder for this).
        string path = kind == "mulaw" ? CompressedWav(kind + ".wav", WaveFormat.CreateMuLawFormat(8000, 1)) : ImaAdpcmWav(kind + ".wav");
        using (var check = new WaveFileReader(path)) Assert.NotEqual(WaveFormatEncoding.Pcm, check.WaveFormat.Encoding);

        var (x, decoder) = SoundLoader.LoadWith(path, Win);
        Assert.IsType<AcmWavDecoder>(decoder);
        Assert.InRange(x.Length, Sr - 2000, Sr + 1000); // whole ADPCM blocks: a little over 1 s
        Assert.InRange(Signals.EstimatePitch(x), 430, 450);
        Assert.InRange(Signals.Peak(x), 0.35, 0.65);
        // The portable list can't: it says so in a friendly way (ffmpeg would take it where installed).
        Assert.Throws<SoundLoadException>(() => SoundLoader.Load(path, new ISoundDecoder[] { new WavAiffDecoder() }));
    }

    [Fact]
    public void Mp3_IsDecodedByMediaFoundation_AndAcmCanToo()
    {
        if (!Mp3.Supported)
        {
            Console.WriteLine("Skipped: no MP3 encoder on this machine");
            return;
        }
        string mp3 = P("which.mp3");
        Mp3.Write(mp3, Signals.Sine(440, 1));
        var (x, decoder) = SoundLoader.LoadWith(mp3, Win);
        Assert.IsType<MediaFoundationDecoder>(decoder); // what 0.2's AudioFileReader used for MP3
        Assert.InRange(Signals.EstimatePitch(x), 430, 450);
        var acm = new AcmMp3Decoder().Decode(mp3);
        Assert.InRange(acm.Length, x.Length - 4000, x.Length + 4000);
        Assert.InRange(Signals.EstimatePitch(acm), 430, 450);
    }
}
