using System.IO;
using Avalonia.Controls;

namespace EasyVocal;

/// <summary>
/// The updater's wiring (<c>Updater.cs</c> does the work): the check when EasyVocal opens, and <c>--update-now</c>.
/// Never in the smoke and e2e tests (they never touch the network), and a build run from the source code never
/// installs (only <c>--update-check</c> makes it look).
/// </summary>
internal sealed partial class AppSession
{
    private UpdateCoordinator? updates;

    /// <summary>The real updater for this platform (its release file, unpacking, helper script and launcher).</summary>
    private static UpdateService RealUpdateService(IUpdaterPlatform platform)
    {
        var current = UpdateRules.RunningVersion(typeof(AppSession).Assembly);
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, platform.ExeName);
        return new UpdateService(platform, new GitHubUpdateSource("EasyVocal-updater/" + current), platform.Launcher, current, exe,
            UpdateService.DefaultWorkRoot, AppLog.Info);
    }

    /// <summary>Builds the updater for a normal launch (null in the tests).</summary>
    private void SetUpUpdates(AppArgs args, string dataFolder, AppController c)
    {
        if (args.TestMode || options.Hosted) return;
        bool sourceBuild = UpdateRules.IsSourceBuild(AppContext.BaseDirectory);
        updates = new UpdateCoordinator(c, RealUpdateService(c.Platform.Updater),
            canCheck: !sourceBuild || args.UpdateCheck, canInstall: !sourceBuild, forceCheck: args.UpdateCheck,
            visibleWindow: VisibleUpdateOwner,
            balloon: (text, click) => tray?.ShowBalloon(text, click),
            quit: () => FireAndForget.Watch(QuitAsync(ask: false), "Quitting for the update"),
            logFile: Path.Combine(dataFolder, "update.log"),
            relaunchArguments: c.Platform.Updater.RelaunchArguments(args.DataFolder));
        c.Updates = updates;
        if (sourceBuild) AppLog.Info("Running from the source code: updates are " + (args.UpdateCheck ? "only checked (--update-check)" : "off"));
    }

    /// <summary>The window to put the update dialog over (the one in front), or null while EasyVocal is only in the tray.</summary>
    private Window? VisibleUpdateOwner()
    {
        if (quitting || window is not { IsVisible: true }) return null;
        foreach (var w in AppWindow.OpenWindows)
            if (w.IsActive && w.IsVisible) return w;
        return window;
    }

    /// <summary>In the background, once the window (and the first-run questions) are up. Never throws.</summary>
    private async void CheckForUpdatesOnOpen()
    {
        if (updates == null) return;
        try { await updates.CheckOnOpenAsync(); }
        catch (Exception ex) { AppLog.Error("The update check failed", ex); }
    }

    /// <summary>
    /// <c>--update-now</c>: no window. Looks for the latest release and, when it is newer, downloads it and hands over
    /// to the helper (which opens the new EasyVocal), all without asking. Logs to the data folder. Exit codes:
    /// 0 updated or already the latest, 1 failed, 3 a build run from the source code, 4 EasyVocal is running.
    /// </summary>
    public static int RunUpdateNow(AppArgs args, string dataFolder)
    {
        AppLog.Init(dataFolder);
        int code;
        try { code = Task.Run(() => UpdateNowAsync(args, dataFolder)).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            AppLog.Error("--update-now failed", ex);
            code = 1;
        }
        AppLog.Info("--update-now: exit code " + code);
        return code;
    }

    private static async Task<int> UpdateNowAsync(AppArgs args, string dataFolder)
    {
        var updater = PlatformFactory.ForThisComputer().Updater;
        var service = RealUpdateService(updater);
        AppLog.Info($"--update-now: EasyVocal {service.Current} at {service.ExePath}");
        if (UpdateRules.IsSourceBuild(AppContext.BaseDirectory))
        {
            AppLog.Info("--update-now: this EasyVocal runs from its source code: it never installs updates");
            return 3;
        }
        if (SingleInstance.IsRunning())
        {
            AppLog.Info("--update-now: EasyVocal is running: close it first");
            return 4;
        }
        service.CleanStale();
        var check = await service.CheckAsync(skippedVersion: null, manual: true);
        if (check.Status == UpdateStatus.UpToDate) return 0;
        if (check.Status != UpdateStatus.Available || check.Asset == null) return 1;
        var payload = await service.DownloadAsync(check.Asset, null, CancellationToken.None);
        if (!service.CanSelfUpdate())
        {
            AppLog.Info("--update-now: this EasyVocal can't replace itself here: get the new version from " + check.Release!.PageUrl);
            return 1;
        }
        service.StartInstall(payload, Environment.ProcessId, Path.Combine(dataFolder, "update.log"), updater.RelaunchArguments(args.DataFolder));
        return 0;
    }
}
