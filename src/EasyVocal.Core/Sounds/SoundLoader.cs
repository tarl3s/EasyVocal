using System;
using System.Collections.Generic;
using System.IO;
using EasyVocal.Core.Dsp;

namespace EasyVocal.Core.Sounds;

/// <summary>A sound file could not be used. The message is a friendly English sentence meant for <c>L.T</c>.</summary>
public sealed class SoundLoadException : Exception
{
    public SoundLoadException(string message) : base(message) { }
    public SoundLoadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Reads a sound file into mono samples at <see cref="AudioFormat.SampleRate"/> through the decoders in
/// <see cref="SoundDecoders.Current"/>: everywhere WAV, AIFF, MP3 (NLayer) and OGG (NVorbis); on Windows also FLAC,
/// M4A/AAC, WMA and the sound of a video (MP4, MOV, WMV…) through Media Foundation; elsewhere those through
/// <c>ffmpeg</c> when it is installed.
/// </summary>
public static class SoundLoader
{
    /// <summary>Longest sound we accept, in seconds.</summary>
    public const double MaxSeconds = 600;

    public const string Damaged = "We couldn't read that sound file. It may be damaged.";

    /// <summary>No decoder on this computer reads that kind of file (macOS/Linux without ffmpeg).</summary>
    public const string NeedsFfmpeg =
        "EasyVocal can't open this kind of file by itself on this computer. Install the free program ffmpeg and try again.";

    public static readonly string[] Extensions =
    {
        ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".mp4", ".m4v", ".mov", ".wmv",
    };

    /// <summary>Video files: only their sound is used (and kept, as a WAV, when one is added to the soundboard).</summary>
    public static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".wmv" };

    /// <summary>True when the file is a video (by its extension).</summary>
    public static bool IsVideo(string? path) => HasExtension(path, VideoExtensions);

    /// <summary>The extensions as a file-dialog pattern: "*.wav;*.mp3;…".</summary>
    public static string DialogPattern => "*" + string.Join(";*", Extensions);

    /// <summary>True when the file's extension is one <see cref="Load(string)"/> reads.</summary>
    public static bool IsSupported(string? path) => HasExtension(path, Extensions);

    private static bool HasExtension(string? path, string[] extensions) =>
        !string.IsNullOrEmpty(path) && Array.IndexOf(extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>Reads the file with this platform's decoders (<see cref="SoundDecoders.Current"/>).</summary>
    public static float[] Load(string path) => Load(path, SoundDecoders.Current);

    /// <summary>
    /// Reads the file with the first of <paramref name="decoders"/> that can: each one that takes the extension is tried
    /// in turn; a <see cref="SoundLoadException"/> (too long, empty…) is the answer, any other failure moves on to the next.
    /// </summary>
    public static float[] Load(string path, IReadOnlyList<ISoundDecoder> decoders)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new SoundLoadException("We couldn't find that sound file.");
        if (!IsSupported(path))
            throw new SoundLoadException("That kind of file isn't supported. Please pick a sound or video file (WAV, MP3, OGG, FLAC, M4A, WMA, MP4…).");
        string ext = Path.GetExtension(path).ToLowerInvariant();

        Exception? failure = null;
        bool tried = false;
        foreach (var decoder in decoders)
        {
            if (!decoder.CanDecode(ext)) continue;
            tried = true;
            try { return Clean(decoder.Decode(path)); }
            catch (SoundLoadException) { throw; }
            catch (Exception ex) { failure = ex; }
        }
        if (!tried) throw new SoundLoadException(NeedsFfmpeg);
        throw new SoundLoadException(Damaged, failure!);
    }

    private static float[] Clean(float[] data)
    {
        if (data.Length == 0) throw new SoundLoadException("That sound file is empty.");
        if (data.Length / (double)AudioFormat.SampleRate > MaxSeconds) throw TooLong();
        for (int i = 0; i < data.Length; i++)
            if (!float.IsFinite(data[i])) data[i] = 0;
        return data;
    }

    public static SoundLoadException TooLong() =>
        new("That sound is too long. Please pick one shorter than 10 minutes.");
}
