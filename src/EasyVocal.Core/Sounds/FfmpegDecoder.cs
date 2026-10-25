using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using EasyVocal.Core.Dsp;

namespace EasyVocal.Core.Sounds;

/// <summary>
/// Anything else through the <c>ffmpeg</c> program, when it is installed (found on PATH, or in Homebrew's folders on
/// macOS, where apps opened from Finder don't get the shell's PATH). It decodes the first sound track straight to
/// mono 48 kHz 32-bit float on its standard output; nothing is written to disk. EasyVocal never installs it.
/// One deadline covers the whole run: past it ffmpeg is stopped and the answer is "took too long".
/// </summary>
public sealed class FfmpegDecoder : ISoundDecoder
{
    /// <summary>How long one file may take to decode before ffmpeg is stopped.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait for ffmpeg to go away once it was told to stop.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(2);

    private static readonly object ProbeSync = new();
    private static string? found; // guarded by ProbeSync

    private readonly string? explicitPath;
    private readonly bool useSearch;

    /// <summary>The ffmpeg on this computer: looked for again while there is none, so installing it needs no restart.</summary>
    public FfmpegDecoder() => useSearch = true;

    /// <summary>A given program standing for ffmpeg (null = none: the decoder takes nothing). For tests.</summary>
    public FfmpegDecoder(string? ffmpegPath, TimeSpan? timeout = null)
    {
        explicitPath = ffmpegPath;
        Timeout = timeout ?? DefaultTimeout;
    }

    public TimeSpan Timeout { get; } = DefaultTimeout;

    public string Name => "ffmpeg";

    /// <summary>The program that runs, or null when ffmpeg isn't there.</summary>
    public string? ProgramPath => useSearch ? Located() : explicitPath;

    /// <summary>True when an ffmpeg program was found.</summary>
    public bool Available => ProgramPath is { } p && File.Exists(p);

    /// <summary>Any supported file, when ffmpeg is there (it comes after the built-in decoders).</summary>
    public bool CanDecode(string extension) => Available;

    private static string? Located()
    {
        lock (ProbeSync)
        {
            if (found == null || !File.Exists(found)) found = Find(Environment.GetEnvironmentVariable("PATH"));
            return found;
        }
    }

    /// <summary>
    /// The ffmpeg in one of the folders of <paramref name="pathVariable"/> (or Homebrew's), or null. Snap's folders come
    /// last: a snap ffmpeg is confined and may not be allowed to read the person's files.
    /// </summary>
    public static string? Find(string? pathVariable)
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var folders = new List<string>();
        var snap = new List<string>();
        foreach (var raw in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = raw.Trim('"');
            (dir.StartsWith("/snap/", StringComparison.Ordinal) ? snap : folders).Add(dir);
        }
        if (OperatingSystem.IsMacOS()) folders.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin" });
        if (OperatingSystem.IsLinux())
        {
            folders.AddRange(new[] { "/usr/bin", "/usr/local/bin" });
            snap.Add("/snap/bin");
        }
        folders.AddRange(snap);
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

    /// <summary>
    /// The command-line arguments: quiet, the file as a plain file (<c>file:</c>, so a name is never read as a URL or
    /// protocol), first sound track only, mono 48 kHz f32 on stdout, a little over the length limit.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string path) => new[]
    {
        "-nostdin", "-hide_banner", "-loglevel", "error",
        "-i", "file:" + Path.GetFullPath(path),
        "-map", "0:a:0", "-vn", "-ac", "1", "-ar", AudioFormat.SampleRate.ToString(CultureInfo.InvariantCulture),
        "-t", ((int)SoundLoader.MaxSeconds + 1).ToString(CultureInfo.InvariantCulture),
        "-f", "f32le", "pipe:1",
    };

    public float[] Decode(string path, CancellationToken ct)
    {
        var program = ProgramPath;
        if (program == null || !File.Exists(program)) throw new SoundLoadException(SoundLoader.NeedsFfmpeg);
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in Arguments(path)) psi.ArgumentList.Add(a);

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (errors) if (errors.Length < 2000) errors.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();

        // One deadline (or the caller's cancellation) stops ffmpeg; its stdout then ends and the read below returns.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(Timeout);
        using (stop.Token.Register(() => Kill(process)))
        {
            float[] samples;
            bool tooLong;
            try { samples = ReadFloats(process.StandardOutput.BaseStream, out tooLong); }
            catch (IOException) when (stop.IsCancellationRequested)
            {
                samples = Array.Empty<float>();
                tooLong = false;
            }
            if (tooLong) Kill(process);

            var left = Timeout - watch.Elapsed;
            bool exited = left > TimeSpan.Zero && process.WaitForExit(left);
            if (!exited)
            {
                Kill(process);
                exited = process.WaitForExit(KillWait);
            }
            ct.ThrowIfCancellationRequested();
            if (tooLong) throw SoundLoader.TooLong();
            if (stop.IsCancellationRequested || !exited) throw new SoundLoadException(SoundLoader.TookTooLong);
            // A non-zero exit is a failure even with some output (whatever reached stdout can't be trusted).
            if (process.ExitCode != 0)
            {
                string detail;
                lock (errors) detail = errors.ToString().Trim();
                throw new InvalidDataException("ffmpeg failed (" + process.ExitCode + "): " + detail);
            }
            return samples;
        }
    }

    /// <summary>
    /// Little-endian floats until the end of the stream, straight into a growing array; stops (tooLong) past the length
    /// limit. A cut-off last sample is dropped.
    /// </summary>
    public static float[] ReadFloats(Stream stdout, out bool tooLong)
    {
        tooLong = false;
        long limit = (long)(SoundLoader.MaxSeconds * AudioFormat.SampleRate) + AudioFormat.SampleRate;
        var samples = new float[AudioFormat.SampleRate * 4];
        int count = 0;
        var buffer = new byte[64 * 1024];
        int carry = 0; // bytes of an unfinished sample at the start of buffer
        int n;
        while ((n = stdout.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            int have = carry + n;
            int whole = have / 4;
            if (count + (long)whole > limit)
            {
                tooLong = true;
                break;
            }
            if (count + whole > samples.Length)
                Array.Resize(ref samples, (int)Math.Min(limit, Math.Max(samples.Length * 2L, count + (long)whole)));
            for (int i = 0; i < whole; i++)
                samples[count++] = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(i * 4, 4));
            carry = have - whole * 4;
            if (carry > 0) Buffer.BlockCopy(buffer, whole * 4, buffer, 0, carry);
        }
        if (count != samples.Length) Array.Resize(ref samples, count);
        return samples;
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
    }
}
