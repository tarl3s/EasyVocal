using EasyVocal.Core.Engine;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Sounds;

namespace EasyVocal.E2E;

/// <summary>
/// A pretend platform for the tests that run on any OS (the headless UI tests): the given devices, hotkeys that only
/// exist in memory, a startup entry and a virtual mic that are only pretend. Nothing on the computer is touched.
/// </summary>
internal static class TestPlatform
{
    /// <param name="audio">The devices (the e2e test's <see cref="TestAudioDevices"/>).</param>
    /// <param name="updater">How updates fit this OS (Windows' real one, or a pretend one).</param>
    /// <param name="decoders">The decoders (null = the portable list).</param>
    /// <param name="mp3">The MP3 encoder, or null for none.</param>
    public static PlatformServices Create(IAudioBackend audio, IUpdaterPlatform updater, IReadOnlyList<ISoundDecoder>? decoders = null,
        IMp3Encoder? mp3 = null) =>
        new(audio, new MemoryStartup(), new PlatformFactory.NoShortcut(), new TestVirtualMic(), updater,
            decoders ?? SoundDecoders.Default, mp3, () => new MemoryHotkeys());

    /// <summary>The e2e devices with the cable missing at first (the relaunched run: there).</summary>
    public static TestAudioDevices Devices(bool cablePresent) => new(cablePresent);
}

/// <summary>"Start with Windows" kept in memory.</summary>
internal sealed class MemoryStartup : IStartupService
{
    private bool on;

    /// <summary>What a real entry would start (EasyVocal hidden in the tray).</summary>
    public string Command => "EasyVocal --tray";

    public bool IsEnabled() => on;

    public void Set(bool value) => on = value;

    public bool Refresh() => false;
}

/// <summary>
/// Global hotkeys that only exist in memory: every combination is free unless another action of ours has it, and a
/// "press" (<see cref="SimulatePress"/>) goes through <see cref="Pressed"/> like a real one.
/// </summary>
internal sealed class MemoryHotkeys : IGlobalHotkeys, IHotkeyTestHooks
{
    private readonly Dictionary<int, (string Action, Hotkey Key)> registered = new();
    private int nextId = 1;

    public event Action<string>? Pressed;

    public bool Available => true;

    public string? UnavailableReason => null;

    public bool Register(string actionId, Hotkey key)
    {
        if (registered.Values.Any(r => r.Key == key)) return false;
        registered[nextId++] = (actionId, key);
        return true;
    }

    public HotkeyCheck Check(Hotkey key) => registered.Values.Any(r => r.Key == key) ? HotkeyCheck.Taken : HotkeyCheck.Free;

    public void UnregisterAll()
    {
        registered.Clear();
        nextId = 1;
    }

    public int Count => registered.Count;

    public int HighestId => registered.Count == 0 ? 0 : registered.Keys.Max();

    public bool SimulatePress(string actionId)
    {
        foreach (var (_, r) in registered)
        {
            if (r.Action != actionId) continue;
            Pressed?.Invoke(actionId);
            return true;
        }
        return false;
    }

    public void Dispose() => UnregisterAll();
}

/// <summary>A VB-CABLE look-alike: found by VB-CABLE's device names (the e2e devices use them), never installed for real.</summary>
internal sealed class TestVirtualMic : IVirtualMicProvider
{
    public string Name => "VB-CABLE";

    public string CallAppDeviceName => "CABLE Output (VB-Audio Virtual Cable)";

    public VirtualMicTexts Texts { get; } = new(
        Explanation: UiText.Mark("EasyVocal sends your voice to calls through a free virtual microphone called VB-CABLE."),
        Downloading: UiText.Mark("Downloading VB-CABLE…"),
        InstallerPrompt: UiText.Mark("Allow the installer to make changes, then click Install Driver in its window."),
        InstallManually: UiText.Mark("Download it from the official page, run VBCABLE_Setup_x64.exe, then come back."),
        NotVerified: UiText.Mark("The download could not be verified as VB-Audio's, so EasyVocal did not run it. Download it from the official page, run VBCABLE_Setup_x64.exe, then come back."),
        AutomaticOutput: UiText.Mark("Automatic (CABLE Input)"),
        OutputHint: UiText.Mark("Automatic sends your voice to the virtual microphone (CABLE Input). Change it only if you know why."));

    public string PageUrl => "https://vb-audio.com/Cable/";

    public CableNames Cable => CableNames.VbCable;

    public DeviceInfo? Find(IReadOnlyList<DeviceInfo> outputs) => DeviceRules.FindVirtualCable(Cable, outputs);

    public bool CanInstall => true;

    public bool TakesDefaultsOnInstall => true;

    public Task<InstallResult> InstallAsync(IProgress<double> progress, CancellationToken ct) =>
        Task.FromResult(new InstallResult(InstallStatus.Failed, "the tests never install anything"));

    public bool CanCreate => false;

    public Task<bool> CreateAsync(CancellationToken ct) => Task.FromResult(false);

    public Task RemoveAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>The tests never open a browser.</summary>
    public void OpenPage() { }
}
