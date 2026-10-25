using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Update;

namespace EasyVocal.Core.Platform;

// What EasyVocal needs from the operating system, as interfaces. Core and the app only talk to these; each platform
// project (EasyVocal.Platform.Windows today; macOS and Linux next) implements them. Decoding and MP3 export are
// Core.Sounds.ISoundDecoder and Core.Sounds.IMp3Encoder.

/// <summary>
/// The sound devices: the microphones and playback devices the system reports, opening them, the system's default
/// devices and when the list changes. The app uses the platform's real one; the e2e test swaps in devices that play and
/// record known audio (and fake defaults, so a test never changes the computer's real ones).
/// </summary>
public interface IAudioBackend
{
    /// <summary>Active microphones. Never throws.</summary>
    IReadOnlyList<DeviceInfo> Inputs();

    /// <summary>Active playback devices. Never throws.</summary>
    IReadOnlyList<DeviceInfo> Outputs();

    /// <summary>Opens a microphone by device id. Throws when it can't be opened (unplugged, busy).</summary>
    IAudioInput OpenMic(string id);

    /// <summary>Opens a playback device by device id. Throws when it can't be opened.</summary>
    IAudioOutput OpenSpeaker(string id);

    /// <summary>The system's default playback (or recording) device, for everything else and for calls. Never throws.</summary>
    DefaultIds Defaults(bool playback);

    /// <summary>
    /// Makes this device the system's default for every role (playback or recording, as the device is). False when it
    /// didn't work or the platform can't (where it isn't supported). Never throws.
    /// </summary>
    bool SetDefault(string id);

    /// <summary>The device list may have changed. Raised on any thread: post to the UI thread before listing.</summary>
    event Action? Changed;
}

/// <summary>System-wide hotkeys. <see cref="Pressed"/> (the action id) fires on the UI thread.</summary>
public interface IGlobalHotkeys : IDisposable
{
    event Action<string>? Pressed;

    /// <summary>False when another app already owns that combination (or the platform has no global hotkeys).</summary>
    bool Register(string actionId, Hotkey key);

    /// <summary>True when the system would accept that combination right now (nobody, EasyVocal included, owns it).</summary>
    bool IsFree(Hotkey key);

    /// <summary>Lets go of every hotkey.</summary>
    void UnregisterAll();

    /// <summary>How many hotkeys are registered now.</summary>
    int Count { get; }

    /// <summary>For the tests: the highest internal id in use (0 when none).</summary>
    int HighestId { get; }

    /// <summary>For the e2e test: a press of the action's hotkey through the same path as a real one. False when it has none.</summary>
    bool SimulatePress(string actionId);
}

/// <summary>"Start with Windows" / "Open at login": EasyVocal starts hidden in the tray when the person signs in.</summary>
public interface IStartupService
{
    /// <summary>True when the entry exists (whatever program it points to).</summary>
    bool IsEnabled();

    /// <summary>Adds the entry, or removes it (nothing happens when it isn't there). Throws when the system refuses.</summary>
    void Set(bool on);

    /// <summary>Writes the entry again if it is missing or points to another place (EasyVocal moved). True when it wrote.</summary>
    bool Refresh();
}

/// <summary>The "EasyVocal" entry in the system's app menu (Windows' Start menu, Linux' applications menu).</summary>
public interface IShortcutService
{
    /// <summary>False where there is nothing to create (macOS: the .app in Applications is the entry).</summary>
    bool Supported { get; }

    /// <summary>Where the entry is (or would be).</summary>
    string ShortcutPath { get; }

    bool Exists();

    /// <summary>The program the entry opens, or null when there is none (or it can't be read).</summary>
    string? ReadTarget();

    /// <summary>Creates (or rewrites) the entry.</summary>
    void Create();

    /// <summary>Writes the entry again if it is missing or opens another place. Never throws: the error, or null when all is well.</summary>
    Exception? Refresh();
}

/// <summary>How <see cref="IVirtualMicProvider.InstallAsync"/> ended.</summary>
public enum InstallStatus
{
    /// <summary>The installer ran and was closed (whether the driver is there is up to the system: the window checks the devices).</summary>
    Installed,
    /// <summary>The person said no to the system's permission prompt.</summary>
    DeclinedPermission,
    /// <summary>Stopped by the caller (the window was closed); an installer already running is left alone.</summary>
    Cancelled,
    /// <summary>The downloaded installer is not signed by its maker (or the signature is not trusted): it was not run.</summary>
    NotVerified,
    /// <summary>Download, unpacking or starting failed; <see cref="InstallResult.Detail"/> says why (for the log).</summary>
    Failed,
}

/// <summary>The outcome of an install attempt.</summary>
public readonly record struct InstallResult(InstallStatus Status, string? Detail = null);

/// <summary>
/// The virtual microphone the voice is sent to and the call app picks as its mic: VB-CABLE on Windows (installed by
/// its own installer), BlackHole on macOS (installed by the person), a PipeWire/PulseAudio device EasyVocal creates on Linux.
/// </summary>
public interface IVirtualMicProvider
{
    /// <summary>Its name as the person sees it ("VB-CABLE").</summary>
    string Name { get; }

    /// <summary>The official page about it (opened by "Learn more").</summary>
    string PageUrl { get; }

    /// <summary>The virtual mic's playback side among <paramref name="outputs"/>, or null when it isn't there.</summary>
    DeviceInfo? Find(IReadOnlyList<DeviceInfo> outputs);

    /// <summary>EasyVocal can download and start its installer (<see cref="InstallAsync"/>).</summary>
    bool CanInstall { get; }

    /// <summary>
    /// Downloads, verifies and runs the installer, and completes when it closes. <paramref name="progress"/> gets 0..1.
    /// Only called when the person presses Install.
    /// </summary>
    Task<InstallResult> InstallAsync(IProgress<double> progress, CancellationToken ct);

    /// <summary>EasyVocal makes the device itself while it runs (<see cref="Create"/> / <see cref="Remove"/>).</summary>
    bool CanCreate { get; }

    /// <summary>Creates the virtual mic for this session. False when it didn't work. Never throws.</summary>
    bool Create();

    /// <summary>Removes what <see cref="Create"/> made. Never throws.</summary>
    void Remove();

    /// <summary>Opens <see cref="PageUrl"/> in the browser. Never throws.</summary>
    void OpenPage();
}

/// <summary>How the updater fits this platform: which release file, which program, how the swap is done.</summary>
public interface IUpdaterPlatform
{
    /// <summary>The release files that fit this computer, best first (e.g. <c>EasyVocal-win-x64.zip</c>).</summary>
    IReadOnlyList<string> AssetNames(Architecture os);

    /// <summary>The program file inside the release (<c>EasyVocal.exe</c>).</summary>
    string ExeName { get; }

    /// <summary>The unpacked program looks like one for this system (a Windows exe starts with "MZ").</summary>
    bool LooksLikeProgram(string path);

    /// <summary>The helper script's file name (<c>update.ps1</c>) and encoding.</summary>
    string HelperFileName { get; }

    Encoding HelperEncoding { get; }

    /// <summary>The helper that waits for EasyVocal to close, swaps the program and opens it again.</summary>
    string BuildHelper(HelperPlan plan);

    /// <summary>The command line EasyVocal gets when the helper opens it again (e.g. <c>--data "C:\x y"</c>).</summary>
    string RelaunchArguments(string? dataFolder);
}

/// <summary>One platform's implementations (hotkeys come separately: they need a window of the UI layer).</summary>
public sealed record PlatformServices(
    IAudioBackend Audio,
    IStartupService Startup,
    IShortcutService Shortcut,
    IVirtualMicProvider VirtualMic,
    IUpdaterPlatform Updater);
