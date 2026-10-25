using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using EasyVocal.Core.Storage;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// Startup: one instance only (a second launch shows the first one's window and exits), settings, language,
/// the controller, the main window and the tray. Quitting happens only from the tray (closing the window hides it).
/// <para>Arguments: <c>--data &lt;folder&gt;</c> uses another data folder (development and tests);
/// <c>--tray</c> starts hidden in the tray (what "Start with Windows" launches); <c>--smoketest</c> opens and checks every window, then quits (<c>App.SmokeTest.cs</c>);
/// <c>--e2e</c> runs the end-to-end test on test audio devices (<c>App.E2E.cs</c>). Both tests need <c>--data</c>.
/// <c>--update-now</c> installs the latest release without a window; <c>--update-check</c> makes a source build look for one (<c>App.Update.cs</c>).</para>
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = "EasyVocal.SingleInstance";
    private const string ShowWindowEventName = "EasyVocal.ShowWindow";

    /// <summary>This computer's platform (Windows): the one place the app picks it. The tests swap some parts.</summary>
    private static readonly PlatformServices RealPlatform = WindowsPlatform.Services;

    private Mutex? instanceMutex;
    private EventWaitHandle? showSignal;
    private RegisteredWaitHandle? showWait;
    private AppController? controller;
    private MainWindow? window;
    private Tray? tray;
    private bool asking, quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = AppArgs.Parse(e.Args);
        string dataFolder = args.DataFolder ?? SettingsStore.DefaultFolder;
        bool smokeTest = args.SmokeTest, startHidden = args.StartHidden;

        // The smoke and e2e tests add and remove items: they must never run on the person's real settings.
        if (!args.Allowed)
        {
            Console.Error.WriteLine("--smoketest and --e2e need --data <folder> (a scratch folder): they never run on the real settings.");
            Shutdown(2);
            return;
        }

        // No window: download and install the latest release, then quit (App.Update.cs).
        if (args.UpdateNow)
        {
            RunUpdateNow(args, dataFolder);
            return;
        }

        // Single instance: the first owns the mutex; a later launch wakes it to show its window, then leaves.
        // A test run neither wakes nor waits for the person's own EasyVocal.
        if (!args.TestMode)
        {
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            instanceMutex = new Mutex(true, InstanceMutexName, out bool first);
            if (!first)
            {
                showSignal.Set();
                showSignal.Dispose();
                showSignal = null;
                instanceMutex.Dispose();
                instanceMutex = null;
                Shutdown();
                return;
            }
            showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(() =>
            {
                AppLog.Info("Launched again: showing the window");
                ShowMainWindow();
            }), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        AppLog.Init(dataFolder);
        AppLog.Info("EasyVocal " + Assembly.GetExecutingAssembly().GetName().Version + " starting (data: " + dataFolder + ")");
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => AppLog.Error("Unhandled", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            AppLog.Error("Background task", ev.Exception);
            ev.SetObserved();
        };

        // The e2e test plays and records known audio through test devices (VB-CABLE missing at first, then "installed").
        if (args.E2E) e2eDevices = new E2E.TestAudioDevices(cablePresent: args.E2ERelaunch);
        // A test never changes Windows' real default devices (the smoke test lists the real devices, read-only).
        // ...and never touch the real "Start with Windows" entry.
        var platform = RealPlatform;
        if (args.TestMode) platform = platform with { Audio = (IAudioBackend?)e2eDevices ?? WindowsAudioBackend.ReadOnlyDefaults, Startup = SmokeStartup };
        controller = new AppController(dataFolder, platform);
        // ...and never downloads or runs the VB-CABLE installer: the e2e test "installs" its test cable instead.
        if (args.TestMode) controller.CableInstaller = e2eDevices != null ? e2eDevices.FakeInstallAsync : (_, _) => Task.FromResult(new InstallResult(InstallStatus.Failed, "tests never install VB-CABLE"));
        // Typing in one of our own boxes must not fire a plain-key hotkey: they are let go while a box has the keyboard.
        EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnTextFocusChanged));
        EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnTextFocusChanged));
        // A box can be left without LostKeyboardFocus (its dialog closed while it had the keyboard): look again whenever
        // a window goes away, when another app takes the keyboard, and before the hotkeys are registered.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnWindowUnloaded));
        Deactivated += (_, _) => controller?.SetTyping(false);
        controller.TypingProbe = () => Keyboard.FocusedElement is TextBoxBase { IsVisible: true };
        controller.RefreshStartup();
        var language = L.Resolve(controller.Settings.Language);
        L.Load(language == "en" ? null : ReadLanguage(language), language);

        window = new MainWindow(controller);
        MainWindow = window;
        tray = new Tray(controller, ShowMainWindow, () => Quit(ask: true));
        window.HiddenToTray += () => tray?.ShowStillRunningTip();
        SetUpUpdates(args, dataFolder, controller);
        if (!startHidden) window.Show();

        // Opening the devices takes a moment: let the window paint first.
        var started = controller;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (quitting || window == null) return;
            started.Start();
            // The tests must never touch the real Start menu.
            if (!args.TestMode) OfferStartMenuShortcut(started, startHidden ? null : window);
            // First run: the setup comes first (not in the tests: the smoke test opens it itself, and they must never download anything).
            if (!started.Settings.SetupDone && !args.TestMode) SetupWindow.Open(started, window.IsVisible ? window : null);
            // Then, in the background, a newer release (never in the tests: they never touch the network).
            CheckForUpdatesOnOpen();
        });

        if (smokeTest) RunSmokeTest(dataFolder);
        else if (args.E2E) RunE2E(dataFolder, args.E2ERelaunch);
    }

    /// <summary>Asks once whether to add EasyVocal to the Start menu (not when started hidden at sign-in, and not when the
    /// shortcut is already there); after a "yes", keeps the shortcut pointing at this EasyVocal.exe.</summary>
    private static void OfferStartMenuShortcut(AppController controller, Window? owner)
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
            settings.StartMenuShortcut = AskDialog.Ask(owner,
                L.T("Add EasyVocal to the Start menu, so you can open it from there?"),
                L.T("Add to Start menu"), L.T("No, thanks"), null, out _);
            settings.StartMenuAsked = true;
            controller.Save();
        }
        if (settings.StartMenuShortcut) shortcut.Refresh(); // a failure is logged
    }

    private void OnTextFocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        controller?.SetTyping(e.NewFocus is TextBoxBase);

    // Once the closing window has handed the keyboard on: is a box of ours still typing?
    private void OnWindowUnloaded(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => controller?.SetTyping(controller.TypingProbe?.Invoke() == true));

    /// <summary>Brings the window back from the tray (also when EasyVocal is launched a second time).</summary>
    private void ShowMainWindow()
    {
        if (window == null || quitting) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true; // Windows won't always let a background app take the focus; this at least shows it
        window.Topmost = false;
    }

    /// <summary>Closes everything. With <paramref name="ask"/>, asks first (unless "Don't ask again" was ticked).</summary>
    private void Quit(bool ask)
    {
        if (quitting || asking || controller == null) return;
        if (ask && controller.Settings.ConfirmQuit)
        {
            asking = true;
            bool yes;
            bool dontAsk;
            try
            {
                var owner = window is { IsVisible: true } ? window : null;
                yes = AskDialog.Ask(owner,
                    L.T("Calls using the EasyVocal microphone will hear nothing until you open it again. Quit anyway?"),
                    L.T("Quit"), L.T("Cancel"), L.T("Don't ask again"), out dontAsk);
            }
            finally
            {
                asking = false;
            }
            if (!yes) return;
            if (dontAsk)
            {
                controller.Settings.ConfirmQuit = false;
                controller.Save(); // written by Dispose below
            }
        }
        quitting = true;
        AppLog.Info("Quitting");
        CleanUp();
        Shutdown();
    }

    private void CleanUp()
    {
        tray?.Dispose();
        tray = null;
        if (window != null) WindowPlacer.Remember(window, "main", controller); // saved by Dispose below
        controller?.Dispose(); // stops the audio, unregisters the hotkeys (the window handle is still alive), saves
        controller = null;
        if (window != null)
        {
            window.AllowClose();
            window.Close();
        }
        showWait?.Unregister(null);
        showWait = null;
        showSignal?.Dispose();
        showSignal = null;
        if (instanceMutex != null)
        {
            try { instanceMutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
            instanceMutex.Dispose();
            instanceMutex = null;
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        quitting = true; // Windows is signing out: no questions
        CleanUp();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!quitting && controller != null) CleanUp();
        base.OnExit(e);
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("UI", e.Exception);
        e.Handled = true; // keep running: the voice in the call matters more than one failed click
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
