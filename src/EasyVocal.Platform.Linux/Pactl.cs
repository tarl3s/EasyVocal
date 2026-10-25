using EasyVocal.Platform.Unix;

namespace EasyVocal.Platform.Linux;

/// <summary>One sink or source as <c>pactl list</c> prints it.</summary>
/// <param name="Name">PulseAudio's name (stable: the id saved in the settings).</param>
/// <param name="Description">What the person sees.</param>
/// <param name="IsMonitor">A source that is only another device's monitor (what a sink plays).</param>
public sealed record PulseDevice(string Name, string Description, bool IsMonitor);

/// <summary>A module <c>pactl list short modules</c> lists.</summary>
public sealed record PulseModule(int Id, string Name, string Arguments);

/// <summary>
/// <c>pactl</c> (PulseAudio's, or PipeWire's pipewire-pulse) — the commands EasyVocal runs and how their output (in the C
/// locale) is read. Everything that touches the system goes through an <see cref="ICommandRunner"/>, so the tests check
/// the exact arguments and feed recorded output.
/// </summary>
public sealed class Pactl(ICommandRunner? runner = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static Pactl Default { get; } = new();

    private ICommandRunner Runner => runner ?? CommandRunner.Instance;

    /// <summary>pactl's full path, or null when it isn't installed.</summary>
    public string? Locate() => Runner.Find("pactl", "/usr/bin", "/bin");

    public async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var pactl = Locate();
        if (pactl == null) return CommandResult.Missing("pactl is not installed");
        return await Runner.RunAsync(pactl, arguments, Timeout, ct).ConfigureAwait(false);
    }

    public CommandResult Run(params string[] arguments) => RunAsync(arguments).GetAwaiter().GetResult();

    // ---------- reading ----------

    /// <summary>The sinks or sources of <c>pactl list sinks|sources</c>: each block's Name and Description.</summary>
    public static List<PulseDevice> ParseList(string output)
    {
        var list = new List<PulseDevice>();
        string? name = null, description = null;
        bool monitor = false, inBlock = false;
        void Flush()
        {
            if (inBlock && !string.IsNullOrEmpty(name))
                list.Add(new PulseDevice(name, string.IsNullOrWhiteSpace(description) ? name : description, monitor || name.EndsWith(".monitor", StringComparison.Ordinal)));
            name = description = null;
            monitor = false;
        }
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Sink #", StringComparison.Ordinal) || line.StartsWith("Source #", StringComparison.Ordinal))
            {
                Flush();
                inBlock = true;
                continue;
            }
            if (!inBlock || line.Length == 0 || !char.IsWhiteSpace(line[0])) continue;
            var t = line.Trim();
            // Only the block's own first-level fields (one tab in): properties further in also say "name".
            if (CountLeadingTabs(line) != 1) continue;
            if (t.StartsWith("Name: ", StringComparison.Ordinal)) name = t["Name: ".Length..].Trim();
            else if (t.StartsWith("Description: ", StringComparison.Ordinal)) description = t["Description: ".Length..].Trim();
            else if (t.StartsWith("Monitor of Sink: ", StringComparison.Ordinal))
                monitor = t["Monitor of Sink: ".Length..].Trim() is { Length: > 0 } of && of != "n/a";
        }
        Flush();
        return list;
    }

    private static int CountLeadingTabs(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] == '\t') n++;
        if (n == 0 && line.StartsWith("    ", StringComparison.Ordinal)) return 1; // some builds indent with spaces
        return n;
    }

    /// <summary>"Default Sink:" / "Default Source:" of <c>pactl info</c>, or null.</summary>
    public static string? ParseDefault(string info, bool playback)
    {
        var key = playback ? "Default Sink: " : "Default Source: ";
        foreach (var raw in info.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(key, StringComparison.Ordinal)) return line[key.Length..].Trim() is { Length: > 0 } v && v != "@DEFAULT_SINK@" ? v : null;
        }
        return null;
    }

    /// <summary>The modules of <c>pactl list short modules</c> (id, name, arguments, tab-separated).</summary>
    public static List<PulseModule> ParseModules(string output)
    {
        var list = new List<PulseModule>();
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), out var id)) continue;
            list.Add(new PulseModule(id, parts[1].Trim(), parts.Length > 2 ? parts[2].Trim() : ""));
        }
        return list;
    }

    /// <summary>The module index <c>pactl load-module</c> prints, or null.</summary>
    public static int? ParseModuleId(string output) => int.TryParse(output.Trim(), out var id) && id >= 0 ? id : null;
}
