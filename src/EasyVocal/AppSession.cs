using System.IO;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>What a session is started with.</summary>
/// <param name="Args">The command line.</param>
/// <param name="DataFolder">Where the settings live (<c>--data</c>, else the normal folder).</param>
internal sealed record SessionOptions(AppArgs Args, string DataFolder)
{
    /// <summary>The single-instance lock of a normal launch (it wakes this session when EasyVocal is launched again).</summary>
    public SingleInstance? Instance { get; init; }

    /// <summary>A test host's platform (fake devices, hotkeys, startup entry…); null = this computer's.</summary>
    public PlatformServices? Platform { get; init; }

    /// <summary>Run inside a test host (headless): no tray, and the e2e "relaunch" is a new session in the same process.</summary>
    public bool Hosted { get; init; }

    /// <summary>For a hosted e2e run: starts the second session on the same folder and gives its exit code.</summary>
    public Func<SessionOptions, Task<int>>? RelaunchHosted { get; init; }
}

/// <summary>
/// One run of EasyVocal: settings, language, the controller, the main window and the tray. Quitting happens only from
/// the tray (closing the window hides it).
/// <para>Arguments: <c>--data &lt;folder&gt;</c> uses another data folder (development and tests); <c>--tray</c>
/// starts hidden in the tray (what "Start with Windows" launches); <c>--smoketest</c> opens and checks every window,
/// then quits (<c>AppSession.SmokeTest.cs</c>); <c>--e2e</c> runs the end-to-end test on test audio devices
/// (<c>AppSession.E2E.cs</c>). Both tests need <c>--data</c>. <c>--update-now</c> installs the latest release without a
/// window; <c>--update-check</c> makes a source build look for one (<c>AppSession.Update.cs</c>).</para>
/// </summary>
internal sealed partial class AppSession
{
    private static bool processHooks;

    private readonly SessionOptions options;
    private readonly Action<int> shutdown;
    private AppController? controller;
    private MainWindow? window;
    private Tray? tray;
    private bool asking, quitting, cleaned;

    /// <param name="shutdown">Ends the app (or, in a test host, the session) with this exit code.</param>
    public AppSession(SessionOptions options, Action<int> shutdown)
    {
        this.options = options;
        this.shutdown = shutdown;
    }

    /// <summary>The session whose controller the app-wide keyboard hooks talk to.</summary>
    internal static AppSession? Current { get; private set; }

    internal AppController? Controller => controller;

    internal MainWindow? Main => window;

    private AppArgs Args => options.Args;

    private string DataFolder => options.DataFolder;

    public void Start()
    {
        var args = Args;
        Current = this;
        AppLog.Init(DataFolder);
        AppLog.Info("EasyVocal " + Assembly.GetExecutingAssembly().GetName().Version + " starting on " +
                    System.Runtime.InteropServices.RuntimeInformation.OSDescription + " (data: " + DataFolder + ")");
        HookProcessOnce();

        // The e2e test plays and records known audio through test devices (the virtual mic missing at first, then "installed").
        if (args.E2E) e2eDevices = options.Platform?.Audio as E2E.TestAudioDevices ?? new E2E.TestAudioDevices(cablePresent: args.E2ERelaunch);
        var platform = options.Platform ?? TestPlatformFor(PlatformFactory.ForThisComputer());
        controller = new AppController(DataFolder, platform);
        // ...and never downloads or runs the virtual mic's installer: the e2e test "installs" its test cable instead.
        if (args.TestMode)
            controller.CableInstaller = e2eDevices != null ? e2eDevices.FakeInstallAsync
                : (_, _) => Task.FromResult(new InstallResult(InstallStatus.Failed, "tests never install the virtual microphone"));
        controller.TypingProbe = AppWindow.TextBoxHasKeyboard;
        controller.RefreshStartup();
        var language = L.Resolve(controller.Settings.Language);
        L.Load(language == "en" ? null : ReadLanguage(language), language);

        window = new MainWindow(controller);
        if (!options.Hosted)
        {
            try { tray = new Tray(controller, ShowMainWindow, () => _ = QuitAsync(ask: true)); }
            catch (Exception ex) { AppLog.Error("The tray icon could not be made", ex); }
        }
        window.HiddenToTray += () => tray?.ShowStillRunningTip();
        options.Instance?.OnShowRequested(() => Dispatcher.UIThread.Post(() =>
        {
            AppLog.Info("Launched again: showing the window");
            ShowMainWindow();
        }));
        SetUpUpdates(args, DataFolder, controller);
        bool startHidden = args.StartHidden && tray != null; // no tray to come back from: show the window
        if (!startHidden) window.Show();

        // Opening the devices takes a moment: let the window paint first.
        var started = controller;
        Dispatcher.UIThread.Post(async () =>
        {
            if (quitting || window == null) return;
            started.Start();
            try
            {
                // The tests must never touch the real Start menu.
                if (!args.TestMode) await OfferStartMenuShortcutAsync(started, startHidden ? null : window);
                // First run: the setup comes first (not in the tests: the smoke test opens it itself, and they must never download anything).
                if (!started.Settings.SetupDone && !args.TestMode && !quitting) await SetupWindow.OpenAsync(started, window.IsVisible ? window : null);
            }
            catch (Exception ex) { AppLog.Error("The first-run questions failed", ex); }
            // Then, in the background, a newer release (never in the tests: they never touch the network).
            CheckForUpdatesOnOpen();
        }, DispatcherPriority.Background);

        if (args.SmokeTest) RunSmokeTest(DataFolder);
        else if (args.E2E) RunE2E(DataFolder, args.E2ERelaunch);
    }

    /// <summary>
    /// The platform a test run uses: never changes the system's real default devices (the smoke test lists the real
    /// devices, read-only), never touches the real "Start with Windows" entry.
    /// </summary>
    private PlatformServices TestPlatformFor(PlatformServices real)
    {
        if (!Args.TestMode) return real;
        var audio = (IAudioBackend?)e2eDevices ?? PlatformFactory.ReadOnlyAudio(real);
        return real with { Audio = audio, Startup = SmokeStartup() };
    }

    /// <summary>App-wide hooks, once per process (a hosted relaunch starts a second session in the same process).</summary>
    private static void HookProcessOnce()
    {
        if (processHooks) return;
        processHooks = true;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Error("UI", e.Exception);
            e.Handled = true; // keep running: the voice in the call matters more than one failed click
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => AppLog.Error("Unhandled", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            AppLog.Error("Background task", ev.Exception);
            ev.SetObserved();
        };
        // Typing in one of our own boxes must not fire a plain-key hotkey: they are let go while a box has the keyboard.
        InputElement.GotFocusEvent.AddClassHandler<TextBox>((_, _) => Current?.controller?.SetTyping(true));
        InputElement.LostFocusEvent.AddClassHandler<TextBox>((_, _) => Current?.LookAtTyping());
    }

    /// <summary>
    /// Once the keyboard has moved on: is a box of ours still typing? Also after a window closes (a box can be left
    /// without LostFocus when its dialog closes) and when another app takes the keyboard.
    /// </summary>
    internal void LookAtTyping() =>
        Dispatcher.UIThread.Post(() => controller?.SetTyping(AppWindow.TextBoxHasKeyboard()), DispatcherPriority.Input);

    /// <summary>Asks once whether to add EasyVocal to the system's app menu (not when started hidden at sign-in, and not when
    /// the shortcut is already there); after a "yes", keeps the shortcut pointing at this EasyVocal.</summary>
    private static async Task OfferStartMenuShortcutAsync(AppController controller, Window? owner)
    {
        var settings = controller.Settings;
        var shortcut = controller.Platform.Shortcut;
        if (!shortcut.Supported) return;
        if (!settings.StartMenuAsked)
        {
            if (owner == null) return; // ask when the person opens it, not at sign-in
            if (shortcut.Exists())
            {
                settings.StartMenuAsked = settings.StartMenuShortcut = true;
                controller.Save();
                return;
            }
            var answer = await AskDialog.AskAsync(owner,
                L.T("Add EasyVocal to the Start menu, so you can open it from there?"),
                L.T("Add to Start menu"), L.T("No, thanks"), null);
            settings.StartMenuShortcut = answer.Yes;
            settings.StartMenuAsked = true;
            controller.Save();
        }
        if (settings.StartMenuShortcut) shortcut.Refresh(); // a failure is logged
    }

    /// <summary>Brings the window back from the tray (also when EasyVocal is launched a second time).</summary>
    private void ShowMainWindow()
    {
        if (window == null || quitting) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true; // the system won't always let a background app take the focus; this at least shows it
        window.Topmost = false;
    }

    /// <summary>
    /// Closes everything. With <paramref name="ask"/>, asks first (unless "Don't ask again" was ticked). Without
    /// <paramref name="end"/> the app is left running once everything is closed (the e2e test relaunches first).
    /// True when it quit.
    /// </summary>
    private async Task<bool> QuitAsync(bool ask, bool end = true)
    {
        if (quitting || asking || controller == null) return false;
        if (ask && controller.Settings.ConfirmQuit)
        {
            asking = true;
            AskDialog.Answer answer;
            try
            {
                var owner = window is { IsVisible: true } ? window : null;
                answer = await AskDialog.AskAsync(owner,
                    L.T("Calls using the EasyVocal microphone will hear nothing until you open it again. Quit anyway?"),
                    L.T("Quit"), L.T("Cancel"), L.T("Don't ask again"));
            }
            finally
            {
                asking = false;
            }
            if (!answer.Yes || controller == null || quitting) return false;
            if (answer.Checked)
            {
                controller.Settings.ConfirmQuit = false;
                controller.Save(); // written by Dispose below
            }
        }
        quitting = true;
        AppLog.Info("Quitting");
        CleanUp();
        if (end) shutdown(Environment.ExitCode);
        return true;
    }

    private void CleanUp()
    {
        if (cleaned) return;
        cleaned = true;
        tray?.Dispose();
        tray = null;
        if (window != null) WindowPlacer.Remember(window, "main", controller); // saved by Dispose below
        AppWindow.CloseAllExcept(window); // dialogs still open (a quit from the tray while one is up)
        controller?.Dispose(); // stops the audio, unregisters the hotkeys, saves
        controller = null;
        if (window != null)
        {
            window.AllowClose();
            window.Close();
        }
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>The system is ending the session (signing out): no questions.</summary>
    public void OnSystemShutdown()
    {
        if (cleaned) return;
        quitting = true;
        CleanUp();
    }

    public void OnExit()
    {
        if (!cleaned) CleanUp();
    }

    /// <summary>The embedded <c>Lang/&lt;code&gt;.json</c>, or null (English).</summary>
    private static string? ReadLanguage(string code)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EasyVocal.Lang." + code + ".json");
            if (stream == null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            AppLog.Error("Reading the " + code + " translation failed", ex);
            return null;
        }
    }
}
