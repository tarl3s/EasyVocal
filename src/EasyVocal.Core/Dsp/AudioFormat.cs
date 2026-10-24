namespace EasyVocal.Core.Dsp;

/// <summary>The one audio format every stage works in: mono float samples.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;

    /// <summary>10 ms at 48 kHz.</summary>
    public const int BlockSize = 480;
}
