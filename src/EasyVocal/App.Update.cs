using System.IO;
using System.Threading;
using System.Windows;

namespace EasyVocal;

/// <summary>
/// The updater's wiring (<c>Updater.cs</c> does the work): the check when EasyVocal opens, and <c>--update-now</c>.
/// Never in the smoke and e2e tests (they never touch the network), and a build run from the source code never
/// installs (only <c>--update-check</c> makes it look).
/// </summary>
public partial class App
{
    private UpdateCoordinator? updates;

    private static UpdateService RealUpdateService()
    {
        var current = UpdateRules.RunningVersion(typeof(App).Assembly);
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, WindowsPlatform.Updater.ExeName);
        return new UpdateService(WindowsPlatform.Updater, new GitHubUpdateSource("EasyVocal-updater/" + current), new PowerShellLauncher(), current, exe,
            UpdateService.DefaultWorkRoot, AppLog.Info);
    }

    /// <summary>Builds the updater for a normal launch (null in the tests).</summary>
    private void SetUpUpdates(AppArgs args, string dataFolder, AppController c)
    {
        if (args.TestMode) return;
        bool sourceBuild = UpdateRules.IsSourceBuild(AppContext.BaseDirectory);
        updates = new UpdateCoordinator(c, RealUpdateService(),
            canCheck: !sourceBuild || args.UpdateCheck, canInstall: !sourceBuild, forceCheck: args.UpdateCheck,
            visibleWindow: VisibleUpdateOwner,
            balloon: (text, click) => tray?.ShowBalloon(text, click),
            quit: () => Quit(ask: false),
            logFile: Path.Combine(dataFolder, "update.log"),
            relaunchArguments: WindowsPlatform.Updater.RelaunchArguments(args.DataFolder));
        c.Updates = updates;
        if (sourceBuild) AppLog.Info("Running from the source code: updates are " + (args.UpdateCheck ? "only checked (--update-check)" : "off"));
    }

    /// <summary>The window to put the update dialog over (the one in front), or null while EasyVocal is only in the tray.</summary>
    private Window? VisibleUpdateOwner()
    {
        if (quitting || window is not { IsVisible: true }) return null;
        foreach (Window w in Windows)
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
    private void RunUpdateNow(AppArgs args, string dataFolder)
    {
        AppLog.Init(dataFolder);
        Dispatcher.InvokeAsync(async () =>
        {
            int code;
            try { code = await UpdateNowAsync(args, dataFolder); }
            catch (Exception ex)
            {
                AppLog.Error("--update-now failed", ex);
                code = 1;
            }
            AppLog.Info("--update-now: exit code " + code);
            Shutdown(code);
        });
    }

    private static async Task<int> UpdateNowAsync(AppArgs args, string dataFolder)
    {
        var service = RealUpdateService();
        AppLog.Info($"--update-now: EasyVocal {service.Current} at {service.ExePath}");
        if (UpdateRules.IsSourceBuild(AppContext.BaseDirectory))
        {
            AppLog.Info("--update-now: this EasyVocal runs from its source code: it never installs updates");
            return 3;
        }
        if (Mutex.TryOpenExisting(InstanceMutexName, out var running))
        {
            running.Dispose();
            AppLog.Info("--update-now: EasyVocal is running: close it first");
            return 4;
        }
        service.CleanStale();
        var check = await service.CheckAsync(skippedVersion: null, manual: true);
        if (check.Status == UpdateStatus.UpToDate) return 0;
        if (check.Status != UpdateStatus.Available || check.Asset == null) return 1;
        var exe = await service.DownloadAsync(check.Asset, null, CancellationToken.None);
        service.StartInstall(exe, Environment.ProcessId, Path.Combine(dataFolder, "update.log"), WindowsPlatform.Updater.RelaunchArguments(args.DataFolder));
        return 0;
    }
}
