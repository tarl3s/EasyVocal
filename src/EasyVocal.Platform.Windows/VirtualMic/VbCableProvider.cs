using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Windows;

/// <summary>Windows' virtual microphone: VB-CABLE, installed by its official, signature-checked installer.</summary>
public sealed class VbCableProvider : IVirtualMicProvider
{
    public static VbCableProvider Instance { get; } = new();

    public string Name => "VB-CABLE";

    public string PageUrl => VbCableInstaller.PageUrl;

    public DeviceInfo? Find(IReadOnlyList<DeviceInfo> outputs) => DeviceRules.FindVirtualCable(outputs);

    public bool CanInstall => true;

    public Task<InstallResult> InstallAsync(IProgress<double> progress, CancellationToken ct) =>
        VbCableInstaller.DownloadAndRunAsync(progress, ct);

    /// <summary>VB-CABLE is a driver: EasyVocal never creates or removes the device itself.</summary>
    public bool CanCreate => false;

    public bool Create() => false;

    public void Remove() { }

    public void OpenPage() => VbCableInstaller.OpenOfficialPage();
}
