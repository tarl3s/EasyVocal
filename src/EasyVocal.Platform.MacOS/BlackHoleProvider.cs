using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;
using EasyVocal.Platform.Unix;

namespace EasyVocal.Platform.MacOS;

/// <summary>
/// macOS' virtual microphone: BlackHole 2ch (free, GPL, by Existential Audio), never bundled. Install: with Homebrew
/// (<c>brew install blackhole-2ch</c>, run in the background; its installer asks for the Mac's password through a small
/// dialog), else its official page opens. The call app picks "BlackHole 2ch" as its microphone. Installing it never
/// changes the Mac's default devices.
/// </summary>
public sealed class BlackHoleProvider(ICommandRunner? runner = null) : IVirtualMicProvider
{
    public const string DeviceName = "BlackHole 2ch";
    public const string Formula = "blackhole-2ch";
    public const string OfficialPage = "https://existential.audio/blackhole/";
    private static readonly string[] BrewFolders = { "/opt/homebrew/bin", "/usr/local/bin" };
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    public static BlackHoleProvider Instance { get; } = new();

    private ICommandRunner Runner => runner ?? CommandRunner.Instance;

    public string Name => "BlackHole";

    public string CallAppDeviceName => DeviceName;

    public VirtualMicTexts Texts { get; } = new(
        Explanation: UiText.Mark("EasyVocal sends your voice to calls through a free virtual microphone called BlackHole 2ch."),
        Downloading: UiText.Mark("Installing BlackHole with Homebrew…"),
        InstallerPrompt: UiText.Mark("If your Mac asks for your password, type it to let BlackHole install."),
        InstallManually: UiText.Mark("Download BlackHole 2ch from its official page and install it, then come back."),
        NotVerified: UiText.Mark("Download BlackHole 2ch from its official page and install it, then come back."),
        AutomaticOutput: UiText.Mark("Automatic (BlackHole 2ch)"),
        OutputHint: UiText.Mark("Automatic sends your voice to the virtual microphone (BlackHole 2ch). Change it only if you know why."));

    public string PageUrl => OfficialPage;

    public CableNames Cable => CableNames.BlackHole;

    public DeviceInfo? Find(IReadOnlyList<DeviceInfo> outputs) => DeviceRules.FindVirtualCable(Cable, outputs);

    /// <summary>Always offered: with Homebrew it installs, without it the official page opens (the setup's fallback).</summary>
    public bool CanInstall => true;

    public bool TakesDefaultsOnInstall => false;

    /// <summary>Homebrew's <c>brew</c>, or null when it isn't installed.</summary>
    public string? Brew() => Runner.Find("brew", BrewFolders);

    /// <summary>The command the install runs.</summary>
    public static IReadOnlyList<string> InstallArguments { get; } = new[] { "install", Formula };

    public async Task<InstallResult> InstallAsync(IProgress<double> progress, CancellationToken ct)
    {
        var brew = Brew();
        if (brew == null) return new InstallResult(InstallStatus.Failed, "Homebrew isn't installed"); // the setup opens the page
        string? askpass = null;
        try
        {
            askpass = WriteAskpass();
            progress.Report(1); // nothing to download first: the setup shows the "password" sentence now
            AppLog.Info("Installing BlackHole: " + brew + " " + string.Join(" ", InstallArguments));
            var env = new Dictionary<string, string>
            {
                ["SUDO_ASKPASS"] = askpass, // Homebrew runs the installer with sudo -A: the password comes from a dialog
                ["HOMEBREW_NO_AUTO_UPDATE"] = "1",
                ["HOMEBREW_NO_INSTALL_CLEANUP"] = "1",
            };
            var result = await Runner.RunAsync(brew, InstallArguments, InstallTimeout, ct, env).ConfigureAwait(false);
            return Outcome(result, ct.IsCancellationRequested);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallResult(InstallStatus.Failed, ex.Message);
        }
        finally
        {
            if (askpass != null) TryDelete(Path.GetDirectoryName(askpass)!);
        }
    }

    /// <summary>What brew's ending means for the setup.</summary>
    public static InstallResult Outcome(CommandResult result, bool cancelled)
    {
        if (cancelled) return new InstallResult(InstallStatus.Cancelled, "stopped");
        if (result.Ok) return new InstallResult(InstallStatus.Installed);
        var text = result.Output + "\n" + result.Error;
        if (text.Contains("already installed", StringComparison.OrdinalIgnoreCase)) return new InstallResult(InstallStatus.Installed);
        if (text.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no password was provided", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("a password is required", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("User canceled", StringComparison.OrdinalIgnoreCase))
            return new InstallResult(InstallStatus.DeclinedPermission, Last(text));
        return new InstallResult(InstallStatus.Failed, "brew exit " + result.ExitCode + ": " + Last(text));
    }

    private static string Last(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }

    /// <summary>The askpass helper sudo runs for the password: a macOS dialog (osascript), the typed text on stdout.</summary>
    public static string AskpassScript { get; } =
        "#!/bin/sh\n" +
        "exec /usr/bin/osascript -e 'display dialog \"EasyVocal is installing BlackHole, the free virtual microphone. Type your Mac password to allow it:\" " +
        "default answer \"\" with hidden answer with title \"EasyVocal\" with icon caution' -e 'text returned of result'\n";

    private static string WriteAskpass()
    {
        var folder = Path.Combine(Path.GetTempPath(), "EasyVocal_askpass_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(folder, "askpass.sh");
        File.WriteAllText(path, AskpassScript);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return path;
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception) { /* a temp file: the system cleans it */ }
    }

    public bool CanCreate => false;

    public Task CleanStaleAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> CreateAsync(CancellationToken ct) => Task.FromResult(false);

    public Task RemoveAsync(CancellationToken ct) => Task.CompletedTask;

    public void OpenPage() => Opener.Open(OfficialPage, Runner);
}
