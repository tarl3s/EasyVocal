using System.Runtime.InteropServices;
using System.Text;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Sounds;
using EasyVocal.Platform.Linux;
using EasyVocal.Platform.MacOS;
using EasyVocal.Platform.Windows;

namespace EasyVocal;

/// <summary>
/// The one place the app picks the computer's platform: <see cref="WindowsPlatform"/>, <see cref="MacPlatform"/> or
/// <see cref="LinuxPlatform"/>; anything else gets <see cref="Unsupported"/> (the window opens with no devices).
/// </summary>
internal static class PlatformFactory
{
    public static PlatformServices ForThisComputer()
    {
        if (OperatingSystem.IsWindows()) return WindowsPlatform.Services;
        if (OperatingSystem.IsMacOS()) return MacPlatform.Services;
        if (OperatingSystem.IsLinux()) return LinuxPlatform.Services(AppIconBytes);
        return Unsupported;
    }

    /// <summary>EasyVocal's .ico (the Linux applications menu entry takes its largest PNG), or null.</summary>
    private static byte[]? AppIconBytes()
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://EasyVocal/Assets/EasyVocal.ico"));
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Error("The icon for the applications menu could not be read", ex);
            return null;
        }
    }

    /// <summary>The same devices, but the system's default devices are never changed (the smoke test).</summary>
    public static IAudioBackend ReadOnlyAudio(PlatformServices real) => new ReadOnlyDefaults(real.Audio);

    /// <summary>A computer EasyVocal has no platform for yet: no devices, no hotkeys, nothing installed.</summary>
    public static PlatformServices Unsupported { get; } = new(
        new NoDevices(), new NoStartup(), new NoShortcut(), new NoVirtualMic(), new NoUpdater(),
        SoundDecoders.Default, null, _ => new NoHotkeys());

    private sealed class ReadOnlyDefaults(IAudioBackend inner) : IAudioBackend
    {
        public IReadOnlyList<DeviceInfo> Inputs() => inner.Inputs();
        public IReadOnlyList<DeviceInfo> Outputs() => inner.Outputs();
        public IAudioInput OpenMic(string id) => inner.OpenMic(id);
        public IAudioOutput OpenSpeaker(string id) => inner.OpenSpeaker(id);
        public DefaultIds Defaults(bool playback) => inner.Defaults(playback);
        public bool SetDefault(string id) => false;

        public event Action? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }
    }

    private sealed class NoDevices : IAudioBackend
    {
        public IReadOnlyList<DeviceInfo> Inputs() => Array.Empty<DeviceInfo>();
        public IReadOnlyList<DeviceInfo> Outputs() => Array.Empty<DeviceInfo>();
        public IAudioInput OpenMic(string id) => throw new InvalidOperationException("No audio devices on this platform yet.");
        public IAudioOutput OpenSpeaker(string id) => throw new InvalidOperationException("No audio devices on this platform yet.");
        public DefaultIds Defaults(bool playback) => new(null, null);
        public bool SetDefault(string id) => false;
        public event Action? Changed { add { } remove { } }
    }

    private sealed class NoStartup : IStartupService
    {
        public bool IsEnabled() => false;
        public void Set(bool on) { if (on) throw new PlatformNotSupportedException(); }
        public bool Refresh() => false;
    }

    internal sealed class NoShortcut : IShortcutService
    {
        public bool Supported => false;
        public string ShortcutPath => "";
        public bool Exists() => false;
        public string? ReadTarget() => null;
        public void Create() => throw new PlatformNotSupportedException();
        public bool Refresh() => false;
    }

    private sealed class NoVirtualMic : IVirtualMicProvider
    {
        public string Name => "virtual microphone";
        public string CallAppDeviceName => "EasyVocal";
        public VirtualMicTexts Texts { get; } = new(
            UiText.Mark("EasyVocal sends your voice to calls through a virtual microphone."),
            "", "", "", "", UiText.Mark("Automatic"), "");
        public string PageUrl => "https://github.com/tarl3s/EasyVocal";
        public CableNames Cable => CableNames.VbCable;

        public DeviceInfo? Find(IReadOnlyList<DeviceInfo> outputs) => DeviceRules.FindVirtualCable(Cable, outputs);
        public bool CanInstall => false;
        public bool TakesDefaultsOnInstall => false;
        public Task<InstallResult> InstallAsync(IProgress<double> progress, CancellationToken ct) =>
            Task.FromResult(new InstallResult(InstallStatus.Failed, "not on this platform yet"));
        public bool CanCreate => false;
        public Task CleanStaleAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<bool> CreateAsync(CancellationToken ct) => Task.FromResult(false);
        public Task RemoveAsync(CancellationToken ct) => Task.CompletedTask;
        public void OpenPage() { }
    }

    private sealed class NoUpdater : IUpdaterPlatform
    {
        public IReadOnlyList<string> AssetNames(Architecture arch) => Array.Empty<string>();
        public string ExeName => "EasyVocal";
        public string InstallRoot(string exePath) => exePath;
        public bool IsInstallRoot(string path) => false;
        public bool CanSelfUpdate(string exePath, Action<string>? log = null) => false;
        public string Unpack(string archivePath, string folder, CancellationToken ct) => throw new UpdateException(UpdateProblem.NotAZip, "not on this platform yet");
        public string HelperFileName => "update.sh";
        public Encoding HelperEncoding => new UTF8Encoding(false);
        public string BuildHelper(HelperPlan plan) => throw new PlatformNotSupportedException();
        public IHelperLauncher Launcher { get; } = new NoLauncher();
        public string RelaunchArguments(string? dataFolder) => "";

        private sealed class NoLauncher : IHelperLauncher
        {
            public void Start(string scriptPath) => throw new PlatformNotSupportedException();
        }
    }

    internal sealed class NoHotkeys : IGlobalHotkeys
    {
        public event Action<string>? Pressed { add { } remove { } }
        public bool Available => false;
        public string? UnavailableReason => "Global hotkeys aren't available on this computer.";
        public bool Register(string actionId, Hotkey key) => false;
        public HotkeyCheck Check(Hotkey key) => HotkeyCheck.Unknown;
        public void UnregisterAll() { }
        public int Count => 0;
        public void Dispose() { }
    }
}
