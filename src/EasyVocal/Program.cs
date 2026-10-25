using Avalonia;
using EasyVocal.Core.Storage;

namespace EasyVocal;

/// <summary>
/// The entry point. What needs no window happens here, before Avalonia starts: the tests' "--data or nothing" rule
/// (exit code 2), <c>--update-now</c>, and the single-instance hand-over (a second launch wakes the first one and
/// leaves). Then the app runs with an explicit shutdown: closing the window only hides it in the tray.
/// </summary>
public static class Program
{
    /// <summary>What this launch is for (read by <see cref="App"/> once Avalonia is up); null when a test host builds the app.</summary>
    internal static SessionOptions? Launch { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var parsed = AppArgs.Parse(args);
        // The smoke and e2e tests add and remove items: they must never run on the person's real settings.
        if (!parsed.Allowed)
        {
            Console.Error.WriteLine("--smoketest and --e2e need --data <folder> (a scratch folder): they never run on the real settings.");
            return 2;
        }
        string dataFolder = parsed.DataFolder ?? SettingsStore.DefaultFolder;

        // No window: download and install the latest release, then quit.
        if (parsed.UpdateNow) return AppSession.RunUpdateNow(parsed, dataFolder);

        // One EasyVocal: a later launch wakes the first to show its window, then leaves. A test run neither wakes nor
        // waits for the person's own EasyVocal.
        SingleInstance? instance = null;
        if (!parsed.TestMode)
        {
            instance = SingleInstance.TryAcquire();
            if (instance == null) return 0; // the running EasyVocal was asked to show its window
        }
        try
        {
            Launch = new SessionOptions(parsed, dataFolder) { Instance = instance };
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            // The UI could not even start: say so somewhere a person (or a test) can find it.
            Console.Error.WriteLine(ex);
            AppLog.Init(dataFolder);
            AppLog.Error("EasyVocal could not start", ex);
            return 1;
        }
        finally
        {
            instance?.Dispose();
        }
    }

    /// <summary>Avalonia's set-up (also used by the visual designer).</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().With(App.Fonts()).LogToTrace();
}
