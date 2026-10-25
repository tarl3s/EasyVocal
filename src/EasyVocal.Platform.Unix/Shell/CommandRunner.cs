using System.Diagnostics;
using System.Text;

namespace EasyVocal.Platform.Unix;

/// <summary>What a command printed and how it ended.</summary>
/// <param name="ExitCode">Its exit code; -1 when it couldn't be started or was stopped (timeout, cancellation).</param>
public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    /// <summary>The program isn't installed (or couldn't be started at all).</summary>
    public bool NotFound { get; init; }

    public static CommandResult Missing(string why) => new(-1, "", why) { NotFound = true };
}

/// <summary>Runs the system's command-line tools (pactl, brew, open, xdg-open…). The tests pass a fake that records the calls.</summary>
public interface ICommandRunner
{
    /// <summary>
    /// Runs <paramref name="program"/> with <paramref name="arguments"/> (each passed as is: no shell, no quoting) and
    /// waits at most <paramref name="timeout"/>; a program still running then is killed. Never throws (a cancellation
    /// included): a failure is in the result.
    /// </summary>
    Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? environment = null);

    /// <summary>
    /// Runs a program that keeps printing (<c>pactl subscribe</c>): <paramref name="line"/> gets each line of its output as
    /// it comes (on a background thread) until it exits or <paramref name="ct"/> stops it (it is killed then). The exit
    /// code; -1 when it couldn't start or was stopped. Never throws.
    /// </summary>
    Task<int> StreamAsync(string program, IReadOnlyList<string> arguments, Action<string> line, CancellationToken ct);

    /// <summary>The full path of the executable <paramref name="program"/> on PATH or in <paramref name="extraFolders"/>, or null.</summary>
    string? Find(string program, params string[] extraFolders);

    /// <summary>Starts a program and doesn't wait for it (opening a link, the update helper). False when it couldn't start.</summary>
    bool Start(string program, IReadOnlyList<string> arguments);
}

/// <summary>The real one: <see cref="Process"/>, output in the C locale (English, so it can be read).</summary>
public sealed class CommandRunner : ICommandRunner
{
    // Every await below says ConfigureAwait(false): some callers wait synchronously on another thread.
    public static CommandRunner Instance { get; } = new();

    /// <summary>
    /// The locale the tools run in: English labels (they are read) and, on Linux, UTF-8 for the names in them (a device
    /// called "Fone de ouvido Ñ"). Where a system lacks C.UTF-8 the tool's setlocale fails and it simply stays in C.
    /// </summary>
    public static string Locale { get; } = OperatingSystem.IsLinux() ? "C.UTF-8" : "C";

    private static ProcessStartInfo Info(string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = Locale; // pactl & co. print their labels in English then
        if (environment != null)
            foreach (var (key, value) in environment) psi.Environment[key] = value;
        return psi;
    }

    public async Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Process? process;
        try { process = Process.Start(Info(program, arguments, environment)); }
        catch (Exception ex) { return CommandResult.Missing(ex.Message); }
        if (process == null) return CommandResult.Missing("did not start");
        using (process)
        {
            try { process.StandardInput.Close(); } catch (Exception) { /* it may have ended already */ }
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return new CommandResult(-1, await Quietly(output).ConfigureAwait(false), (ct.IsCancellationRequested ? "cancelled" : "timed out") + ": " + await Quietly(error).ConfigureAwait(false));
            }
            return new CommandResult(process.ExitCode, await Quietly(output).ConfigureAwait(false), await Quietly(error).ConfigureAwait(false));
        }
    }

    public async Task<int> StreamAsync(string program, IReadOnlyList<string> arguments, Action<string> line, CancellationToken ct)
    {
        Process? process;
        try { process = Process.Start(Info(program, arguments, null)); }
        catch (Exception) { return -1; }
        if (process == null) return -1;
        using (process)
        {
            try { process.StandardInput.Close(); } catch (Exception) { /* fine */ }
            _ = process.StandardError.ReadToEndAsync(); // drained, never read
            using var stop = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            });
            try
            {
                while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } text)
                {
                    try { line(text); }
                    catch (Exception ex) { AppLog.Error("Reading " + program + "'s output failed", ex); }
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return ct.IsCancellationRequested ? -1 : process.ExitCode;
            }
            catch (Exception) { return -1; } // stopped, or the pipe broke
        }
    }

    private static async Task<string> Quietly(Task<string> read)
    {
        try { return await read.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception) { return ""; }
    }

    public string? Find(string program, params string[] extraFolders)
    {
        if (program.Contains('/')) return IsExecutable(program) ? program : null;
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(extraFolders);
        foreach (var folder in folders)
        {
            try
            {
                var path = Path.Combine(folder, program);
                if (IsExecutable(path)) return path;
            }
            catch (ArgumentException) { /* a broken PATH entry */ }
        }
        return null;
    }

    /// <summary>A file someone may run (an execute bit set; on Windows, the tests' host, any file).</summary>
    public static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            const UnixFileMode Any = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & Any) != 0;
        }
        catch (Exception) { return false; }
    }

    public bool Start(string program, IReadOnlyList<string> arguments)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = program, UseShellExecute = false };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex)
        {
            AppLog.Info("Starting " + program + " failed: " + ex.Message);
            return false;
        }
    }
}

/// <summary>Opens a link or a settings page with the system's opener (macOS <c>open</c>, Linux <c>xdg-open</c>).</summary>
public static class Opener
{
    /// <summary>Never throws: false (logged) when nothing could open it.</summary>
    public static bool Open(string target, ICommandRunner? runner = null)
    {
        runner ??= CommandRunner.Instance;
        var tool = OperatingSystem.IsMacOS() ? "/usr/bin/open" : runner.Find("xdg-open") ?? "xdg-open";
        bool ok = runner.Start(tool, new[] { target });
        if (!ok) AppLog.Info("Could not open " + target);
        return ok;
    }
}
