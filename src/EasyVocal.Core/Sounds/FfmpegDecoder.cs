using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using EasyVocal.Core.Dsp;

namespace EasyVocal.Core.Sounds;

/// <summary>
/// Anything else through the <c>ffmpeg</c> program, when it is installed (found on PATH, or in Homebrew's folders on
/// macOS, where apps opened from Finder don't get the shell's PATH). It decodes the first sound track straight to
/// mono 48 kHz 32-bit float on its standard output; nothing is written to disk. EasyVocal never installs it.
/// </summary>
public sealed class FfmpegDecoder : ISoundDecoder
{
    /// <summary>How long one file may take to decode before ffmpeg is stopped.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private static readonly Lazy<string?> OnThisComputer = new(() => Find(Environment.GetEnvironmentVariable("PATH")));

    private readonly string? explicitPath;
    private readonly bool useSearch;

    /// <summary>The ffmpeg found on this computer (looked for once).</summary>
    public FfmpegDecoder() => useSearch = true;

    /// <summary>A given ffmpeg program (null = none: the decoder takes nothing). For tests.</summary>
    public FfmpegDecoder(string? ffmpegPath, TimeSpan? timeout = null)
    {
        explicitPath = ffmpegPath;
        Timeout = timeout ?? DefaultTimeout;
    }

    public TimeSpan Timeout { get; } = DefaultTimeout;

    public string Name => "ffmpeg";

    /// <summary>The program that runs, or null when ffmpeg isn't there.</summary>
    public string? ProgramPath => useSearch ? OnThisComputer.Value : explicitPath;

    /// <summary>True when an ffmpeg program was found.</summary>
    public bool Available => ProgramPath is { } p && File.Exists(p);

    /// <summary>Any supported file, when ffmpeg is there (it comes after the built-in decoders).</summary>
    public bool CanDecode(string extension) => Available;

    /// <summary>The ffmpeg in one of the folders of <paramref name="pathVariable"/> (or Homebrew's), or null.</summary>
    public static string? Find(string? pathVariable)
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var folders = new List<string>();
        foreach (var dir in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            folders.Add(dir.Trim('"'));
        if (OperatingSystem.IsMacOS()) folders.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin" });
        if (OperatingSystem.IsLinux()) folders.AddRange(new[] { "/usr/bin", "/usr/local/bin", "/snap/bin" });
        foreach (var dir in folders)
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a broken PATH entry */ }
        }
        return null;
    }

    /// <summary>The command-line arguments: quiet, first sound track only, mono 48 kHz f32 on stdout, a little over the limit.</summary>
    public static IReadOnlyList<string> Arguments(string path) => new[]
    {
        "-nostdin", "-hide_banner", "-loglevel", "error",
        "-i", path,
        "-map", "0:a:0", "-vn", "-ac", "1", "-ar", AudioFormat.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-t", ((int)SoundLoader.MaxSeconds + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-f", "f32le", "pipe:1",
    };

    public float[] Decode(string path)
    {
        var program = ProgramPath;
        if (program == null || !File.Exists(program)) throw new SoundLoadException(SoundLoader.NeedsFfmpeg);

        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        foreach (var a in Arguments(Path.GetFullPath(path))) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var errors = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null && errors.Length < 2000) lock (errors) errors.AppendLine(e.Data); };
        process.BeginErrorReadLine();

        bool timedOut = false, tooLong = false;
        using var timer = new Timer(_ => { timedOut = true; Kill(process); }, null, Timeout, System.Threading.Timeout.InfiniteTimeSpan);
        float[] samples;
        try { samples = ReadFloats(process.StandardOutput.BaseStream, out tooLong); }
        finally
        {
            if (tooLong) Kill(process);
            if (!process.WaitForExit((int)Math.Min(int.MaxValue, Timeout.TotalMilliseconds))) { timedOut = true; Kill(process); }
            timer.Dispose();
        }
        if (tooLong) throw SoundLoader.TooLong();
        if (timedOut) throw new TimeoutException("ffmpeg took longer than " + Timeout.TotalSeconds + " s.");
        process.WaitForExit(); // the stderr reader is done too
        // A non-zero exit is a failure even with some output (whatever reached stdout can't be trusted).
        if (process.ExitCode != 0)
        {
            string detail;
            lock (errors) detail = errors.ToString().Trim();
            throw new InvalidDataException("ffmpeg failed (" + process.ExitCode + "): " + detail);
        }
        return samples;
    }

    /// <summary>Little-endian floats until the end of the stream, stopping (tooLong) past the length limit.</summary>
    public static float[] ReadFloats(Stream stdout, out bool tooLong)
    {
        tooLong = false;
        long limitBytes = ((long)(SoundLoader.MaxSeconds * AudioFormat.SampleRate) + AudioFormat.SampleRate) * 4;
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = stdout.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bytes.Length + n > limitBytes)
            {
                tooLong = true;
                break;
            }
            bytes.Write(buffer, 0, n);
        }
        int count = (int)(bytes.Length / 4);
        var samples = new float[count];
        MemoryMarshal.Cast<byte, float>(bytes.GetBuffer().AsSpan(0, count * 4)).CopyTo(samples);
        if (!BitConverter.IsLittleEndian)
            for (int i = 0; i < count; i++)
                samples[i] = BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(BitConverter.SingleToInt32Bits(samples[i])));
        return samples;
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
    }
}
