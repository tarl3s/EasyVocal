using System.Text.Json;
using EasyVocal.Platform.Unix;

namespace EasyVocal.Platform.Linux;

/// <summary>One sink or source as <c>pactl list</c> prints it.</summary>
/// <param name="Name">PulseAudio's name (stable: the id saved in the settings).</param>
/// <param name="Description">What the person sees.</param>
/// <param name="IsMonitor">A source that is only another device's monitor (what a sink plays).</param>
public sealed record PulseDevice(string Name, string Description, bool IsMonitor);

/// <summary>A module <c>pactl list short modules</c> lists.</summary>
public sealed record PulseModule(int Id, string Name, string Arguments);

/// <summary>The server's default sink and source (null = none, or a placeholder like <c>@DEFAULT_SINK@</c>).</summary>
public sealed record PulseDefaults(string? Sink, string? Source);

/// <summary>What one look through pactl found; <see cref="Ok"/> false when the server didn't answer (keep the last good look).</summary>
public sealed record PulseListing(bool Ok, IReadOnlyList<PulseDevice> Sinks, IReadOnlyList<PulseDevice> Sources, PulseDefaults Defaults)
{
    public static readonly PulseListing Failed = new(false, Array.Empty<PulseDevice>(), Array.Empty<PulseDevice>(), new PulseDefaults(null, null));
}

/// <summary>
/// <c>pactl</c> (PulseAudio's, or PipeWire's pipewire-pulse) — the commands EasyVocal runs and how their output is read:
/// JSON (<c>pactl -f json</c>, PulseAudio 16+ and PipeWire) where it is supported, else the text of the C locale.
/// Everything that touches the system goes through an <see cref="ICommandRunner"/>, so the tests check the exact
/// arguments and feed recorded output.
/// </summary>
public sealed class Pactl
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static Pactl Default { get; } = new();

    private readonly ICommandRunner? runner;
    private int json; // 0 = not known yet, 1 = JSON works, -1 = text only

    /// <param name="json">True/false to fix the format (the tests); null = ask pactl once.</param>
    public Pactl(ICommandRunner? runner = null, bool? json = null)
    {
        this.runner = runner;
        this.json = json switch { true => 1, false => -1, null => 0 };
    }

    private ICommandRunner Runner => runner ?? CommandRunner.Instance;

    /// <summary>pactl's full path, or null when it isn't installed.</summary>
    public string? Locate() => Runner.Find("pactl", "/usr/bin", "/bin");

    /// <summary>The command-line runner (the subscription uses it too).</summary>
    public ICommandRunner CommandLine => Runner;

    public async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var pactl = Locate();
        if (pactl == null) return CommandResult.Missing("pactl is not installed");
        return await Runner.RunAsync(pactl, arguments, Timeout, ct).ConfigureAwait(false);
    }

    public CommandResult Run(params string[] arguments) => RunAsync(arguments).GetAwaiter().GetResult();

    // ---------- looking ----------

    /// <summary>The default sink and source now; null when the server didn't answer.</summary>
    public async Task<PulseDefaults?> DefaultsAsync(CancellationToken ct = default)
    {
        if (await UseJsonAsync(ct).ConfigureAwait(false))
        {
            var j = await RunAsync(new[] { "-f", "json", "info" }, ct).ConfigureAwait(false);
            if (j.Ok && ParseJsonInfo(j.Output) is { } d) return d;
        }
        var info = await RunAsync(new[] { "info" }, ct).ConfigureAwait(false);
        return info.Ok ? new PulseDefaults(ParseDefault(info.Output, playback: true), ParseDefault(info.Output, playback: false)) : null;
    }

    /// <summary>Sinks, sources and the defaults in one look. <see cref="PulseListing.Ok"/> false when any command failed.</summary>
    public PulseListing List()
    {
        try
        {
            var defaults = DefaultsAsync().GetAwaiter().GetResult();
            if (defaults == null) return PulseListing.Failed;
            if (UseJsonAsync(default).GetAwaiter().GetResult())
            {
                var sinks = Run("-f", "json", "list", "sinks");
                var sources = Run("-f", "json", "list", "sources");
                if (sinks.Ok && sources.Ok && ParseJsonList(sinks.Output) is { } js && ParseJsonList(sources.Output) is { } jo)
                    return new PulseListing(true, js, jo, defaults);
            }
            var textSinks = Run("list", "sinks");
            var textSources = Run("list", "sources");
            if (!textSinks.Ok || !textSources.Ok) return PulseListing.Failed;
            return new PulseListing(true, ParseList(textSinks.Output), ParseList(textSources.Output), defaults);
        }
        catch (Exception ex)
        {
            AppLog.Error("Listing the sound devices failed", ex);
            return PulseListing.Failed;
        }
    }

    /// <summary>Whether <c>pactl -f json</c> works (asked once; an older pactl says "invalid option").</summary>
    private async Task<bool> UseJsonAsync(CancellationToken ct)
    {
        if (json != 0) return json == 1;
        var probe = await RunAsync(new[] { "-f", "json", "info" }, ct).ConfigureAwait(false);
        if (probe.NotFound) return false; // no pactl: ask again when there is one
        json = probe.Ok && probe.Output.TrimStart().StartsWith('{') ? 1 : -1;
        return json == 1;
    }

    // ---------- reading: JSON ----------

    /// <summary>The objects of <c>pactl -f json list sinks|sources</c>, or null when it isn't that JSON.</summary>
    public static List<PulseDevice>? ParseJsonList(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<PulseDevice>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (Text(item, "name") is not { Length: > 0 } name) continue;
                var description = Text(item, "description");
                bool monitor = name.EndsWith(".monitor", StringComparison.Ordinal) ||
                               (Text(item, "monitor_of_sink") is { Length: > 0 } of && of != "n/a");
                list.Add(new PulseDevice(name, string.IsNullOrWhiteSpace(description) ? name : description, monitor));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The defaults of <c>pactl -f json info</c>, or null when it isn't that JSON.</summary>
    public static PulseDefaults? ParseJsonInfo(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return new PulseDefaults(Real(Text(doc.RootElement, "default_sink_name")), Real(Text(doc.RootElement, "default_source_name")));
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement item, string key) =>
        item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A device name, or null for nothing or a placeholder (<c>@DEFAULT_SINK@</c>, <c>@DEFAULT_SOURCE@</c>).</summary>
    private static string? Real(string? name) => string.IsNullOrWhiteSpace(name) || name.Trim().StartsWith('@') ? null : name.Trim();

    // ---------- reading: text ----------

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

    /// <summary>"Default Sink:" / "Default Source:" of <c>pactl info</c>, or null (also for a placeholder like <c>@DEFAULT_SINK@</c>).</summary>
    public static string? ParseDefault(string info, bool playback)
    {
        var key = playback ? "Default Sink: " : "Default Source: ";
        foreach (var raw in info.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(key, StringComparison.Ordinal)) return Real(line[key.Length..]);
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

    /// <summary>
    /// One line of <c>pactl subscribe</c> means the device list or the defaults may have changed: a sink or source came
    /// or went, or the server changed (its defaults). Volume changes ("change on sink") don't.
    /// </summary>
    public static bool MattersForDevices(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith("Event '", StringComparison.Ordinal)) return false;
        bool newOrRemove = t.StartsWith("Event 'new'", StringComparison.Ordinal) || t.StartsWith("Event 'remove'", StringComparison.Ordinal);
        if (newOrRemove && (t.Contains(" on sink #", StringComparison.Ordinal) || t.Contains(" on source #", StringComparison.Ordinal))) return true;
        return t.StartsWith("Event 'change' on server", StringComparison.Ordinal);
    }
}
