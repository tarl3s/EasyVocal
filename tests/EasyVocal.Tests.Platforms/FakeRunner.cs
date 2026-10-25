using EasyVocal.Platform.Unix;

namespace EasyVocal.Tests.Platforms;

/// <summary>A pretend command line: records every call and answers from a script of its own; nothing is run.</summary>
public sealed class FakeRunner : ICommandRunner
{
    public sealed record Call(string Program, string[] Arguments, IReadOnlyDictionary<string, string>? Environment);

    public readonly List<Call> Calls = new();
    public readonly List<Call> Started = new();

    /// <summary>The programs that are "installed" (name → full path).</summary>
    public readonly Dictionary<string, string> Installed = new();

    /// <summary>Answers by the arguments joined with spaces; a missing one is exit 0 with no output.</summary>
    public readonly Dictionary<string, CommandResult> Answers = new();

    /// <summary>Runs while a command "runs" (e.g. to look at a file it was given).</summary>
    public Action<Call>? During;

    public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var call = new Call(program, arguments.ToArray(), environment);
        lock (Calls) Calls.Add(call);
        During?.Invoke(call);
        if (ct.IsCancellationRequested) return Task.FromResult(new CommandResult(-1, "", "cancelled"));
        return Task.FromResult(Answers.TryGetValue(string.Join(" ", arguments), out var r) ? r : new CommandResult(0, "", ""));
    }

    public string? Find(string program, params string[] extraFolders) => Installed.TryGetValue(program, out var path) ? path : null;

    public bool Start(string program, IReadOnlyList<string> arguments)
    {
        lock (Started) Started.Add(new Call(program, arguments.ToArray(), null));
        return true;
    }

    /// <summary>The argument lists of the calls so far, joined with spaces.</summary>
    public List<string> Lines()
    {
        lock (Calls) return Calls.Select(c => string.Join(" ", c.Arguments)).ToList();
    }
}

internal static class TestFolders
{
    public static string New(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string path)
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }

    /// <summary>The repository's root (where EasyVocal.sln is).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EasyVocal.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("EasyVocal.sln not found above " + AppContext.BaseDirectory);
    }
}
