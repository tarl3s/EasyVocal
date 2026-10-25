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

    /// <summary>Answers given one after the other (before <see cref="Answers"/>): the first call gets the first one…</summary>
    public readonly Dictionary<string, Queue<CommandResult>> InTurn = new();

    /// <summary>Runs while a command "runs" (e.g. to look at a file it was given).</summary>
    public Action<Call>? During;

    public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var call = new Call(program, arguments.ToArray(), environment);
        lock (Calls) Calls.Add(call);
        During?.Invoke(call);
        if (ct.IsCancellationRequested) return Task.FromResult(new CommandResult(-1, "", "cancelled"));
        var key = string.Join(" ", arguments);
        lock (InTurn)
            if (InTurn.TryGetValue(key, out var queue) && queue.Count > 0) return Task.FromResult(queue.Dequeue());
        return Task.FromResult(Answers.TryGetValue(key, out var r) ? r : new CommandResult(0, "", ""));
    }

    /// <summary>What a streaming command (<c>pactl subscribe</c>) prints: the test writes lines; completing it ends the command.</summary>
    public readonly System.Threading.Channels.Channel<string> StreamLines = System.Threading.Channels.Channel.CreateUnbounded<string>();

    /// <summary>How many times a streaming command was started.</summary>
    public int StreamsStarted;

    public async Task<int> StreamAsync(string program, IReadOnlyList<string> arguments, Action<string> line, CancellationToken ct)
    {
        Interlocked.Increment(ref StreamsStarted);
        lock (Calls) Calls.Add(new Call(program, arguments.ToArray(), null));
        try
        {
            await foreach (var text in StreamLines.Reader.ReadAllAsync(ct)) line(text);
            return 0;
        }
        catch (OperationCanceledException) { return -1; }
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

