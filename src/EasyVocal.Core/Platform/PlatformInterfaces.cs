using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Sounds;
using EasyVocal.Core.Update;

namespace EasyVocal.Core.Platform;

// What EasyVocal needs from the operating system, as interfaces. Core and the app only talk to these; each platform
// project (EasyVocal.Platform.Windows today; macOS and Linux next) implements them and hands them out as one
// PlatformServices.

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

    /// <summary>
    /// Opens a microphone by device id. Throws when it can't be opened (unplugged, busy):
    /// <see cref="MicPermissionDeniedException"/> when the system refuses EasyVocal the microphone (privacy settings).
    /// </summary>
    IAudioInput OpenMic(string id);

    /// <summary>Opens a playback device by device id. Throws when it can't be opened.</summary>
    IAudioOutput OpenSpeaker(string id);

    /// <summary>
    /// The system's default playback (or recording) device. Windows has two roles (everything else, and calls); a system
    /// with one default (macOS, Linux) reports it as both <see cref="DefaultIds.Console"/> and
    /// <see cref="DefaultIds.Communications"/>. Never throws.
    /// </summary>
    DefaultIds Defaults(bool playback);

    /// <summary>
    /// Makes this device the system's default for every role (playback or recording, as the device is). False when it
    /// didn't work or the platform can't (where it isn't supported). Never throws.
    /// </summary>
    bool SetDefault(string id);

    /// <summary>The device list may have changed. Raised on any thread: post to the UI thread before listing.</summary>
    event Action? Changed;
}

/// <summary>The system refused EasyVocal the microphone (Windows' or macOS' privacy settings): the person must allow it.</summary>
public sealed class MicPermissionDeniedException : Exception
{
    public MicPermissionDeniedException() : base("The system doesn't let EasyVocal use the microphone.") { }

    public MicPermissionDeniedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Whether a key combination can be taken.</summary>
public enum HotkeyCheck
{
    /// <summary>Nobody (EasyVocal included) owns it right now.</summary>
    Free,
    /// <summary>Another app (or EasyVocal) owns it.</summary>
    Taken,
    /// <summary>The platform can't tell before trying (it may still work).</summary>
    Unknown,
}

/// <summary>
/// System-wide hotkeys. <see cref="Pressed"/> (the action id) may fire on any thread: the UI layer posts it to its own.
/// Where the platform has none (Wayland), <see cref="Available"/> is false and <see cref="UnavailableReason"/> says why;
/// everything else still works with clicks.
/// </summary>
public interface IGlobalHotkeys : IDisposable
{
    event Action<string>? Pressed;

    /// <summary>The system lets EasyVocal have global hotkeys.</summary>
    bool Available { get; }

    /// <summary>When not <see cref="Available"/>: one English sentence for <c>L.T</c>; null otherwise.</summary>
    string? UnavailableReason { get; }

    /// <summary>False when another app already owns that combination (or there are no global hotkeys).</summary>
    bool Register(string actionId, Hotkey key);

    /// <summary>Whether that combination is free right now.</summary>
    HotkeyCheck Check(Hotkey key);

    /// <summary>Lets go of every hotkey.</summary>
    void UnregisterAll();

    /// <summary>How many hotkeys are registered now.</summary>
    int Count { get; }
}

/// <summary>What the tests look at on a hotkey implementation (not part of what the app needs).</summary>
public interface IHotkeyTestHooks
{
    /// <summary>The highest internal id in use (0 when none).</summary>
    int HighestId { get; }

    /// <summary>A press of the action's hotkey through the same path as a real one. False when it has none.</summary>
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

    /// <summary>Writes the entry again if it is missing or opens another place. Never throws: false (logged) when it failed.</summary>
    bool Refresh();
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
/// The setup's and settings' sentences about one virtual microphone, in English (shown through <c>L.T</c>; each is
/// written with <see cref="UiText.Mark"/> so the translation test finds it).
/// </summary>
/// <param name="Explanation">Step 1 of the setup: what it is, in one sentence.</param>
/// <param name="Downloading">While the installer downloads.</param>
/// <param name="InstallerPrompt">Once the installer starts: what to click in it.</param>
/// <param name="InstallManually">The install couldn't run: how to do it by hand.</param>
/// <param name="NotVerified">The download wasn't signed by its maker.</param>
/// <param name="AutomaticOutput">Settings' "Automatic" call device.</param>
/// <param name="OutputHint">Settings' line under the call device.</param>
public sealed record VirtualMicTexts(string Explanation, string Downloading, string InstallerPrompt, string InstallManually,
    string NotVerified, string AutomaticOutput, string OutputHint);

/// <summary>Marks an English sentence written outside the app (in Core or a platform project) as one the person sees.</summary>
public static class UiText
{
    public static string Mark(string english) => english;
}

/// <summary>
/// The virtual microphone the voice is sent to and the call app picks as its mic: VB-CABLE on Windows (installed by
/// its own installer), BlackHole on macOS (installed by the person), a PipeWire/PulseAudio device EasyVocal creates on
/// Linux while it runs.
/// </summary>
public interface IVirtualMicProvider
{
    /// <summary>Its name as the person sees it ("VB-CABLE").</summary>
    string Name { get; }

    /// <summary>The microphone the person picks in the call app ("CABLE Output (VB-Audio Virtual Cable)").</summary>
    string CallAppDeviceName { get; }

    /// <summary>The setup's and settings' sentences about it.</summary>
    VirtualMicTexts Texts { get; }

    /// <summary>The official page about it (opened by "Learn more").</summary>
    string PageUrl { get; }

    /// <summary>How its devices are named: where the voice goes, and which devices are never a mic or headphones.</summary>
    CableNames Cable { get; }

    /// <summary>The virtual mic's playback side among <paramref name="outputs"/>, or null when it isn't there.</summary>
    DeviceInfo? Find(IReadOnlyList<DeviceInfo> outputs);

    /// <summary>EasyVocal can download and start its installer (<see cref="InstallAsync"/>).</summary>
    bool CanInstall { get; }

    /// <summary>
    /// Its install may make it the system's default devices (VB-CABLE does): EasyVocal remembers the defaults before and
    /// puts them back (the banner's Fix). False = none of that logic runs.
    /// </summary>
    bool TakesDefaultsOnInstall { get; }

    /// <summary>
    /// Downloads, verifies and runs the installer, and completes when it closes. <paramref name="progress"/> gets 0..1.
    /// Only called when the person presses Install.
    /// </summary>
    Task<InstallResult> InstallAsync(IProgress<double> progress, CancellationToken ct);

    /// <summary>EasyVocal makes the device itself while it runs (<see cref="CreateAsync"/> / <see cref="RemoveAsync"/>).</summary>
    bool CanCreate { get; }

    /// <summary>
    /// Creates the virtual mic for this session (at start, and again when it went missing). Already there: true at once.
    /// What an earlier EasyVocal left (it crashed) is removed first; a creation that fails half-way removes what it
    /// made. False when it didn't work. Never throws.
    /// </summary>
    Task<bool> CreateAsync(CancellationToken ct);

    /// <summary>Removes the virtual mic (on quit): what this session made and anything else of EasyVocal's left. Never throws.</summary>
    Task RemoveAsync(CancellationToken ct);

    /// <summary>Opens <see cref="PageUrl"/> in the browser. Never throws.</summary>
    void OpenPage();
}

/// <summary>
/// How the updater fits this platform: which release file, how it is unpacked, what is replaced, how the swap is done.
/// Windows: a zip whose EasyVocal.exe replaces the running one through a PowerShell helper; macOS will replace the
/// .app bundle and Linux the whole folder.
/// </summary>
public interface IUpdaterPlatform
{
    /// <summary>The release files that fit this computer, best first (e.g. <c>EasyVocal-win-x64.zip</c>).</summary>
    IReadOnlyList<string> AssetNames(Architecture arch);

    /// <summary>The program file the person starts (<c>EasyVocal.exe</c>), for messages and checks.</summary>
    string ExeName { get; }

    /// <summary>What an update replaces for the program at <paramref name="exePath"/> (the exe, the .app, the folder).</summary>
    string InstallRoot(string exePath);

    /// <summary>
    /// <paramref name="path"/> (an install root or its .old copy) is something a release of EasyVocal put there (where the
    /// platform's package has its <c>easyvocal-files.txt</c>; on Windows the single exe file). Only those are ever deleted.
    /// </summary>
    bool IsInstallRoot(string path);

    /// <summary>
    /// The running EasyVocal can replace itself (its <see cref="InstallRoot"/> can be written); otherwise the release
    /// page is opened instead. <paramref name="log"/> gets why not.
    /// </summary>
    bool CanSelfUpdate(string exePath, Action<string>? log = null);

    /// <summary>
    /// Unpacks a downloaded release (zip or tar.gz) into <paramref name="folder"/> with the shared safety checks
    /// (<see cref="UpdatePackage"/>) and returns the payload that takes <see cref="InstallRoot"/>'s place. Throws
    /// <see cref="UpdateException"/> when it isn't a release for this computer.
    /// </summary>
    string Unpack(string archivePath, string folder, CancellationToken ct);

    /// <summary>The helper script's file name (<c>update.ps1</c>) and encoding.</summary>
    string HelperFileName { get; }

    Encoding HelperEncoding { get; }

    /// <summary>The helper that waits for EasyVocal to close, swaps <see cref="HelperPlan.Target"/> and opens it again.</summary>
    string BuildHelper(HelperPlan plan);

    /// <summary>Starts the helper script (it outlives EasyVocal).</summary>
    IHelperLauncher Launcher { get; }

    /// <summary>The command line EasyVocal gets when the helper opens it again (e.g. <c>--data "C:\x y"</c>).</summary>
    string RelaunchArguments(string? dataFolder);
}

/// <summary>One platform's implementations.</summary>
/// <param name="Decoders">The order <see cref="SoundLoader"/> tries decoders in.</param>
/// <param name="Mp3Encoder">MP3 export; null where there is none (only WAV is offered).</param>
/// <param name="CreateHotkeys">
/// Global hotkeys, created on the UI thread: given a window handle the UI routes its messages itself (Windows:
/// <c>WindowsGlobalHotkeys.ProcessMessage</c>); null = the platform makes its own (Windows: a message-only window).
/// </param>
public sealed record PlatformServices(
    IAudioBackend Audio,
    IStartupService Startup,
    IShortcutService Shortcut,
    IVirtualMicProvider VirtualMic,
    IUpdaterPlatform Updater,
    IReadOnlyList<ISoundDecoder> Decoders,
    IMp3Encoder? Mp3Encoder,
    Func<IntPtr?, IGlobalHotkeys> CreateHotkeys)
{
    /// <summary>The sentences that name this operating system ("Start with Windows"...).</summary>
    public PlatformTexts Texts { get; init; } = PlatformTexts.ForThisComputer;

    /// <summary>Opens the system's microphone privacy settings (the banner's button when the mic is refused); null = none to open.</summary>
    public Action? OpenMicSettings { get; init; }

    /// <summary>Whether the desktop shows tray icons (asked once at start); null = assume it does. Without one, closing the window minimises it.</summary>
    public Func<bool>? HasTray { get; init; }
}
