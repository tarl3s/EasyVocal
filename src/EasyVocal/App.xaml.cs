using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EasyVocal.Core.Storage;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// Startup: one instance only (a second launch shows the first one's window and exits), settings, language,
/// the controller, the main window and the tray. Quitting happens only from the tray (closing the window hides it).
/// <para>Arguments: <c>--data &lt;folder&gt;</c> uses another data folder (development and tests);
/// <c>--smoketest</c> starts normally, quits by itself after 3 s and logs "Smoke test passed".</para>
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = "EasyVocal.SingleInstance";
    private const string ShowWindowEventName = "EasyVocal.ShowWindow";

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

        string dataFolder = SettingsStore.DefaultFolder;
        bool smokeTest = false;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--data" && i + 1 < e.Args.Length) dataFolder = Path.GetFullPath(e.Args[++i]);
            else if (e.Args[i] == "--smoketest") smokeTest = true;
        }

        // Single instance: the first owns the mutex; a later launch wakes it to show its window, then leaves.
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

        AppLog.Init(dataFolder);
        AppLog.Info("EasyVocal " + Assembly.GetExecutingAssembly().GetName().Version + " starting (data: " + dataFolder + ")");
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => AppLog.Error("Unhandled", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            AppLog.Error("Background task", ev.Exception);
            ev.SetObserved();
        };

        controller = new AppController(dataFolder);
        var language = L.Resolve(controller.Settings.Language);
        L.Load(language == "en" ? null : ReadLanguage(language), language);

        window = new MainWindow(controller);
        MainWindow = window;
        tray = new Tray(controller, ShowMainWindow, () => Quit(ask: true));
        window.HiddenToTray += () => tray?.ShowStillRunningTip();
        window.Show();
        // TODO(Task 10): open the Setup window when !controller.Settings.SetupDone; start hidden with --tray.

        // Opening the devices takes a moment: let the window paint first.
        var started = controller;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!quitting && window != null) started.Start(window);
        });

        if (smokeTest) RunSmokeTest(dataFolder);
    }

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
