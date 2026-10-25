using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Linux;

/// <summary>
/// Linux' virtual microphone, made by EasyVocal itself while it runs (no driver to install): with <c>pactl</c> it loads a
/// null sink "easyvocal_out" (shown as EasyVocal-Output; the voice is played into it) and a remap of its monitor as the
/// source "easyvocal_mic" (shown as EasyVocal-Microphone: what the call app picks). Both are unloaded on quit; ones a
/// crashed EasyVocal left are found by name and unloaded at the next start. Works on PulseAudio and on PipeWire
/// (pipewire-pulse); without pactl the setup says what to install.
/// </summary>
public sealed class PactlVirtualMic : IVirtualMicProvider
{
    public const string SinkName = "easyvocal_out";
    public const string SourceName = "easyvocal_mic";
    public const string SinkDescription = "EasyVocal-Output";
    public const string SourceDescription = "EasyVocal-Microphone";
    public const string ReadmeUrl = "https://github.com/tarl3s/EasyVocal#linux";

    private readonly Pactl pactl;
    private readonly Action? devicesChanged;
    private readonly object sync = new();
    private int? sinkModule, sourceModule;

    /// <param name="devicesChanged">Called after the devices were made or removed (the backend reads its list again).</param>
    public PactlVirtualMic(Pactl? pactl = null, Action? devicesChanged = null)
    {
        this.pactl = pactl ?? Pactl.Default;
        this.devicesChanged = devicesChanged;
    }

    /// <summary><c>pactl load-module</c>'s arguments for the sink the voice goes to.</summary>
    public static IReadOnlyList<string> SinkArguments { get; } = new[]
    {
        "load-module", "module-null-sink", "sink_name=" + SinkName, "sink_properties=device.description=" + SinkDescription,
    };

    /// <summary><c>pactl load-module</c>'s arguments for the microphone the call app picks (the sink's monitor, remapped).</summary>
    public static IReadOnlyList<string> SourceArguments { get; } = new[]
    {
        "load-module", "module-remap-source", "master=" + SinkName + ".monitor", "source_name=" + SourceName,
        "source_properties=device.description=" + SourceDescription,
    };

    public string Name => SourceDescription;

    public string CallAppDeviceName => SourceDescription;

    private static readonly VirtualMicTexts Ready = new(
        Explanation: UiText.Mark("EasyVocal makes its own virtual microphone, EasyVocal-Microphone, while it runs: nothing to install."),
        Downloading: "",
        InstallerPrompt: "",
        InstallManually: UiText.Mark("EasyVocal needs pactl to make its microphone: install pipewire-pulse (or pulseaudio-utils), then open EasyVocal again."),
        NotVerified: "",
        AutomaticOutput: UiText.Mark("Automatic (EasyVocal-Output)"),
        OutputHint: UiText.Mark("Automatic sends your voice to EasyVocal's virtual microphone (EasyVocal-Output). Change it only if you know why."));

    private static readonly VirtualMicTexts NoPactl = Ready with
    {
        Explanation = UiText.Mark("EasyVocal needs pactl to make its microphone: install pipewire-pulse (or pulseaudio-utils), then open EasyVocal again."),
    };

    /// <summary>When pactl is missing the setup's first sentence says what to install.</summary>
    public VirtualMicTexts Texts => pactl.Locate() == null ? NoPactl : Ready;

    public string PageUrl => ReadmeUrl;

    public CableNames Cable => CableNames.EasyVocalLinux;

    public DeviceInfo? Find(IReadOnlyList<DeviceInfo> outputs) => DeviceRules.FindVirtualCable(Cable, outputs);

    public bool CanInstall => false;

    public bool TakesDefaultsOnInstall => false;

    public Task<InstallResult> InstallAsync(IProgress<double> progress, CancellationToken ct) =>
        Task.FromResult(new InstallResult(InstallStatus.Failed, "EasyVocal makes its microphone itself on Linux"));

    public bool CanCreate => true;

    /// <summary>The modules this session loaded (null when none), for the tests.</summary>
    internal (int? Sink, int? Source) Loaded
    {
        get { lock (sync) return (sinkModule, sourceModule); }
    }

    /// <summary>Unloads every module whose arguments name our sink or source (left by an EasyVocal that crashed).</summary>
    public async Task CleanStaleAsync(CancellationToken ct)
    {
        try
        {
            var list = await pactl.RunAsync(new[] { "list", "short", "modules" }, ct).ConfigureAwait(false);
            if (!list.Ok) return;
            var ours = Pactl.ParseModules(list.Output).Where(IsOurs).OrderByDescending(m => m.Id).ToList(); // the remap first
            foreach (var m in ours)
            {
                AppLog.Info("Removing a virtual microphone module a previous EasyVocal left: #" + m.Id + " " + m.Name);
                await pactl.RunAsync(new[] { "unload-module", m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
            }
            if (ours.Count > 0) devicesChanged?.Invoke();
        }
        catch (Exception ex) { AppLog.Error("Cleaning up an old virtual microphone failed", ex); }
    }

    /// <summary>A module of ours: a null sink called <see cref="SinkName"/> or a source called <see cref="SourceName"/>.</summary>
    public static bool IsOurs(PulseModule m) =>
        (m.Name == "module-null-sink" && HasArgument(m.Arguments, "sink_name=" + SinkName)) ||
        (m.Name == "module-remap-source" && HasArgument(m.Arguments, "source_name=" + SourceName));

    private static bool HasArgument(string arguments, string wanted)
    {
        foreach (var part in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (part == wanted) return true;
        return false;
    }

    public async Task<bool> CreateAsync(CancellationToken ct)
    {
        try
        {
            if (pactl.Locate() == null)
            {
                AppLog.Info("pactl isn't installed: no virtual microphone (install pipewire-pulse or pulseaudio-utils)");
                return false;
            }
            var before = await pactl.DefaultsAsync(ct).ConfigureAwait(false);
            var sink = await pactl.RunAsync(SinkArguments, ct).ConfigureAwait(false);
            var sinkId = sink.Ok ? Pactl.ParseModuleId(sink.Output) : null;
            if (sinkId == null)
            {
                AppLog.Info("pactl couldn't make the virtual microphone's output: " + sink.Error.Trim());
                return false;
            }
            lock (sync) sinkModule = sinkId;
            var source = await pactl.RunAsync(SourceArguments, ct).ConfigureAwait(false);
            var sourceId = source.Ok ? Pactl.ParseModuleId(source.Output) : null;
            if (sourceId == null)
            {
                AppLog.Info("pactl couldn't make the virtual microphone: " + source.Error.Trim());
                await RemoveAsync(ct).ConfigureAwait(false);
                return false;
            }
            lock (sync) sourceModule = sourceId;
            await PutDefaultsBackAsync(before, ct).ConfigureAwait(false);
            devicesChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Making the virtual microphone failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Some setups make a new sink or source the default (PipeWire with a "follow the newest device" policy,
    /// module-switch-on-connect): then the computer's sound would go into EasyVocal-Output, or calls would hear
    /// EasyVocal-Microphone as the default mic. Only that, our own effect, is undone: the defaults from before come back.
    /// </summary>
    private async Task PutDefaultsBackAsync(PulseDefaults? before, CancellationToken ct)
    {
        if (before == null) return;
        var after = await pactl.DefaultsAsync(ct).ConfigureAwait(false);
        if (after == null) return;
        if (IsOwnDevice(after.Sink) && before.Sink is { } sink && !IsOwnDevice(sink))
        {
            AppLog.Info("The new virtual microphone became the default output: putting back " + sink);
            await pactl.RunAsync(new[] { "set-default-sink", sink }, ct).ConfigureAwait(false);
        }
        if (IsOwnDevice(after.Source) && before.Source is { } source && !IsOwnDevice(source))
        {
            AppLog.Info("The new virtual microphone became the default input: putting back " + source);
            await pactl.RunAsync(new[] { "set-default-source", source }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One of the devices EasyVocal makes (the sink, its monitor, the remapped source).</summary>
    private static bool IsOwnDevice(string? device) =>
        device is SinkName or SourceName or SinkName + ".monitor";

    public async Task RemoveAsync(CancellationToken ct)
    {
        int? sink, source;
        lock (sync)
        {
            (sink, source) = (sinkModule, sourceModule);
            sinkModule = sourceModule = null;
        }
        try
        {
            foreach (var id in new[] { source, sink }) // the remap before the sink it reads from
                if (id is { } m)
                    await pactl.RunAsync(new[] { "unload-module", m.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
            if (sink != null || source != null) devicesChanged?.Invoke();
        }
        catch (Exception ex) { AppLog.Error("Removing the virtual microphone failed", ex); }
    }

    public void OpenPage() => Unix.Opener.Open(PageUrl);
}
