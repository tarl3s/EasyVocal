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

    /// <summary>The full path of <paramref name="program"/> on PATH or in <paramref name="extraFolders"/>, or null.</summary>
    string? Find(string program, params string[] extraFolders);

    /// <summary>Starts a program and doesn't wait for it (opening a link, the update helper). False when it couldn't start.</summary>
    bool Start(string program, IReadOnlyList<string> arguments);
}

/// <summary>The real one: <see cref="Process"/>, output in the C locale (English, so it can be read).</summary>
public sealed class CommandRunner : ICommandRunner
{
    public static CommandRunner Instance { get; } = new();

    public async Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? environment = null)
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
        psi.Environment["LC_ALL"] = "C"; // pactl & co. print their labels in English then
        if (environment != null)
            foreach (var (key, value) in environment) psi.Environment[key] = value;
        Process? process;
        try { process = Process.Start(psi); }
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
                return new CommandResult(-1, await Quietly(output), (ct.IsCancellationRequested ? "cancelled" : "timed out") + ": " + await Quietly(error));
            }
            return new CommandResult(process.ExitCode, await Quietly(output), await Quietly(error));
        }
    }

    private static async Task<string> Quietly(Task<string> read)
    {
        try { return await read.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception) { return ""; }
    }

    public string? Find(string program, params string[] extraFolders)
    {
        if (program.Contains('/')) return File.Exists(program) ? program : null;
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(extraFolders);
        foreach (var folder in folders)
        {
            try
            {
                var path = Path.Combine(folder, program);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { /* a broken PATH entry */ }
        }
        return null;
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
