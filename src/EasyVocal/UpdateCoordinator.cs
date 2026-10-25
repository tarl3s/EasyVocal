using System.Windows;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The updater's place in the app: the check when EasyVocal opens (a dialog when the window is up, a tray balloon when
/// it started hidden), Settings' "Check now", and the hand-over: start the helper, then quit the normal way.
/// The App builds it only outside the tests; the smoke test builds its own with a fake <see cref="UpdateService"/>.
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly AppController controller;
    private readonly UpdateService service;
    private readonly Func<Window?> visibleWindow;
    private readonly Action<string, Action> balloon;
    private readonly Action quit;
    private readonly string logFile, relaunchArguments;
    private bool offering;

    /// <param name="canCheck">False in a build run from the source code (unless <c>--update-check</c>).</param>
    /// <param name="canInstall">False in a build run from the source code: the dialog only tells.</param>
    /// <param name="forceCheck"><c>--update-check</c>: check on open even when the setting is off.</param>
    /// <param name="visibleWindow">The window to put the dialog over, or null while EasyVocal is only in the tray.</param>
    /// <param name="balloon">Shows a tray balloon whose click runs the action.</param>
    /// <param name="quit">Quits the normal way, without asking.</param>
    public UpdateCoordinator(AppController controller, UpdateService service, bool canCheck, bool canInstall, bool forceCheck,
        Func<Window?> visibleWindow, Action<string, Action> balloon, Action quit, string logFile, string relaunchArguments)
    {
        this.controller = controller;
        this.service = service;
        CanCheck = canCheck;
        CanInstall = canInstall;
        ForceCheck = forceCheck;
        this.visibleWindow = visibleWindow;
        this.balloon = balloon;
        this.quit = quit;
        this.logFile = logFile;
        this.relaunchArguments = relaunchArguments;
    }

    public bool CanCheck { get; }
    public bool CanInstall { get; }
    public bool ForceCheck { get; }

    /// <summary>When EasyVocal opens: quietly ask GitHub; tell the person only when there is something new.</summary>
    public async Task CheckOnOpenAsync()
    {
        if (!CanCheck || !(controller.Settings.CheckForUpdates || ForceCheck)) return;
        if (CanInstall) service.CleanStale();
        var check = await service.CheckAsync(controller.Settings.SkippedUpdateVersion, manual: false);
        if (check.Status != UpdateStatus.Available) return;
        var window = visibleWindow();
        if (window != null) Offer(window, check);
        else balloon(L.F("EasyVocal {0} is available — click to update", check.Release!.Version), () => Offer(visibleWindow(), check));
    }

    /// <summary>Settings' "Check now": the text to show there, or null when the update dialog was shown instead.</summary>
    public async Task<string?> CheckNowAsync(Window owner)
    {
        if (!CanCheck) return L.T("This EasyVocal runs from its source code, so it doesn't update itself.");
        var check = await service.CheckAsync(controller.Settings.SkippedUpdateVersion, manual: true);
        switch (check.Status)
        {
            case UpdateStatus.UpToDate: return L.T("You have the latest version");
            case UpdateStatus.Failed: return L.T("Couldn't check for updates. Try again later.");
            default:
                Offer(owner, check);
                return null;
        }
    }

    /// <summary>The update dialog; after Update now, the helper starts and EasyVocal quits.</summary>
    public void Offer(Window? owner, UpdateCheck check)
    {
        if (offering || check.Release == null || check.Asset == null) return;
        offering = true;
        try
        {
            var dialog = new UpdateWindow(controller, service, check.Release, check.Asset, CanInstall);
            dialog.ShowFor(owner);
            if (dialog.PreparedExe != null) Install(dialog.PreparedExe, owner);
        }
        finally
        {
            offering = false;
        }
    }

    private void Install(string newExe, Window? owner)
    {
        try
        {
            service.StartInstall(newExe, Environment.ProcessId, logFile, relaunchArguments);
        }
        catch (Exception ex)
        {
            var problem = ex is UpdateException u ? u.Problem : UpdateProblem.Launch;
            AppLog.Error("Starting the update failed (" + problem + ")", ex);
            AskDialog.Tell(owner is { IsVisible: true } ? owner : null, UpdateWindow.ProblemText(problem));
            return;
        }
        AppLog.Info("Quitting so the update can be installed");
        quit();
    }
}
