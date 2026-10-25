using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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

    /// <summary>A decoder (ffmpeg) was still busy with the file after its time limit and was stopped.</summary>
    public const string TookTooLong = "That sound took too long to open.";

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

    /// <summary>Reads the file with the portable decoders (<see cref="SoundDecoders.Default"/>). The app passes its platform's list.</summary>
    public static float[] Load(string path) => Load(path, SoundDecoders.Default, CancellationToken.None);

    /// <summary>Reads the file with the first of <paramref name="decoders"/> that can (see <see cref="LoadWith"/>).</summary>
    public static float[] Load(string path, IReadOnlyList<ISoundDecoder> decoders, CancellationToken ct = default) =>
        LoadWith(path, decoders, ct).Samples;

    /// <summary>
    /// Reads the file with the first of <paramref name="decoders"/> that gives a usable sound, and says which one did.
    /// Each decoder that takes the extension is tried in turn: a failure, an empty result or "too long" moves on to the
    /// next one. Only when none gave a sound is the answer "empty" / "too long" (whatever a decoder said first), or
    /// "damaged"; no decoder for the extension at all = "install ffmpeg". A cancellation throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static (float[] Samples, ISoundDecoder Decoder) LoadWith(string path, IReadOnlyList<ISoundDecoder> decoders, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new SoundLoadException("We couldn't find that sound file.");
        if (!IsSupported(path))
            throw new SoundLoadException("That kind of file isn't supported. Please pick a sound or video file (WAV, MP3, OGG, FLAC, M4A, WMA, MP4…).");
        string ext = Path.GetExtension(path).ToLowerInvariant();

        SoundLoadException? answer = null;
        Exception? failure = null;
        bool tried = false;
        foreach (var decoder in decoders)
        {
            if (!decoder.CanDecode(ext)) continue;
            tried = true;
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = decoder.Decode(path, ct);
                if (data.Length == 0) answer ??= Empty();
                else if (data.Length / (double)AudioFormat.SampleRate > MaxSeconds) answer ??= TooLong();
                else return (Clean(data), decoder);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (SoundLoadException ex) { answer ??= ex; }
            catch (Exception ex) { failure ??= ex; }
        }
        if (!tried) throw new SoundLoadException(NeedsFfmpeg);
        if (answer != null) throw answer;
        throw new SoundLoadException(Damaged, failure!);
    }

    private static float[] Clean(float[] data)
    {
        for (int i = 0; i < data.Length; i++)
            if (!float.IsFinite(data[i])) data[i] = 0;
        return data;
    }

    public static SoundLoadException Empty() => new("That sound file is empty.");

    public static SoundLoadException TooLong() =>
        new("That sound is too long. Please pick one shorter than 10 minutes.");
}
