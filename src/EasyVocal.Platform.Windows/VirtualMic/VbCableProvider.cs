using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Windows;

/// <summary>Windows' virtual microphone: VB-CABLE, installed by its official, signature-checked installer.</summary>
public sealed class VbCableProvider : IVirtualMicProvider
{
    public static VbCableProvider Instance { get; } = new();

    public string Name => "VB-CABLE";

    /// <summary>The recording end of VB-CABLE as Windows names it: what a call app must pick as its microphone.</summary>
    public string CallAppDeviceName => "CABLE Output (VB-Audio Virtual Cable)";

    public VirtualMicTexts Texts { get; } = new(
        Explanation: UiText.Mark("EasyVocal sends your voice to calls through a free virtual microphone called VB-CABLE."),
        Downloading: UiText.Mark("Downloading VB-CABLE…"),
        InstallerPrompt: UiText.Mark("Allow the installer to make changes, then click Install Driver in its window."),
        InstallManually: UiText.Mark("Download it from the official page, run VBCABLE_Setup_x64.exe, then come back."),
        NotVerified: UiText.Mark("The download could not be verified as VB-Audio's, so EasyVocal did not run it. Download it from the official page, run VBCABLE_Setup_x64.exe, then come back."),
        AutomaticOutput: UiText.Mark("Automatic (CABLE Input)"),
        OutputHint: UiText.Mark("Automatic sends your voice to the virtual microphone (CABLE Input). Change it only if you know why."));

    public string PageUrl => VbCableInstaller.PageUrl;

    public CableNames Cable => CableNames.VbCable;

    public DeviceInfo? Find(IReadOnlyList<DeviceInfo> outputs) => DeviceRules.FindVirtualCable(Cable, outputs);

    public bool CanInstall => true;

    /// <summary>VB-CABLE's installer may make it Windows' default playback device and microphone.</summary>
    public bool TakesDefaultsOnInstall => true;

    public Task<InstallResult> InstallAsync(IProgress<double> progress, CancellationToken ct) =>
        VbCableInstaller.DownloadAndRunAsync(progress, ct);

    /// <summary>VB-CABLE is a driver: EasyVocal never creates or removes the device itself.</summary>
    public bool CanCreate => false;

    public Task CleanStaleAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> CreateAsync(CancellationToken ct) => Task.FromResult(false);

    public Task RemoveAsync(CancellationToken ct) => Task.CompletedTask;

    public void OpenPage() => VbCableInstaller.OpenOfficialPage();
}
