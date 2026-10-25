using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace EasyVocal;

/// <summary>
/// What every EasyVocal window shares: the icon, the theme's window look, a place on screen (<see cref="WindowPlacer"/>),
/// the list of open windows (the tests find dialogs there), a dialog result (<see cref="Accepted"/>), and showing as a
/// dialog: over its owner, else over the main window when that is on screen, else on its own (EasyVocal is only in the
/// tray) — and then at most one window of a kind (<see cref="SingleKind"/>).
/// </summary>
public class AppWindow : Window
{
    private static readonly List<AppWindow> open = new();
    private static readonly Dictionary<string, (AppWindow Window, Task Done)> single = new();
    private static WindowIcon? icon;
    private static bool iconTried;

    public AppWindow()
    {
        if (AppIcon is { } i) Icon = i;
        Opened += (_, _) =>
        {
            if (!open.Contains(this)) open.Add(this);
        };
        Closed += (_, _) =>
        {
            open.Remove(this);
            if (SingleKind is { } kind && single.TryGetValue(kind, out var s) && ReferenceEquals(s.Window, this)) single.Remove(kind);
            AppSession.Current?.LookAtTyping();
        };
        // The keyboard may have moved to or from one of our text boxes while another app had it (Alt-Tab).
        Activated += (_, _) => AppSession.Current?.LookAtTyping();
        Deactivated += (_, _) => AppSession.Current?.LookAtTyping();
    }

    /// <summary>Styled as a plain Window (the theme's "Window" style applies to every subclass).</summary>
    protected override Type StyleKeyOverride => typeof(Window);

    /// <summary>The windows on screen now, oldest first.</summary>
    public static IReadOnlyList<AppWindow> OpenWindows => open.ToArray();

    /// <summary>The window dialogs go over when their owner isn't on screen (the main window; set by the session).</summary>
    public static Func<Window?>? MainWindowSource { get; set; }

    /// <summary>The app's icon (null when the platform can't load icons).</summary>
    public static WindowIcon? AppIcon
    {
        get
        {
            if (iconTried) return icon;
            iconTried = true;
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://EasyVocal/Assets/EasyVocal.ico"));
                icon = new WindowIcon(stream);
            }
            catch (Exception ex) { AppLog.Error("The window icon could not be loaded", ex); }
            return icon;
        }
    }

    /// <summary>
    /// Windows of one kind (Setup, Settings, Update) are never open twice: showing another one brings the open one to the
    /// front and waits for it instead. Null = no limit.
    /// </summary>
    protected virtual string? SingleKind => null;

    /// <summary>True once the window was closed by its OK / Save / Done.</summary>
    public bool Accepted { get; private set; }

    /// <summary>Closed by <see cref="CloseForQuit"/>: whatever it is doing, it must let go.</summary>
    protected bool Quitting { get; private set; }

    /// <summary>This window wasn't shown: one of its kind was open already (<see cref="ShowAsync"/> waited for that one).</summary>
    public bool WasDuplicate { get; private set; }

    /// <summary>Closes as accepted.</summary>
    protected void Accept()
    {
        Accepted = true;
        Close();
    }

    /// <summary>A Cancel button: closes, not accepted.</summary>
    protected void CancelClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Closes even when the window would rather stay (EasyVocal is quitting).</summary>
    public void CloseForQuit()
    {
        Quitting = true;
        Close();
    }

    /// <summary>Keeps the window on screen and where the person left it (<paramref name="controller"/> null = only on screen).</summary>
    protected void Place(string key, AppController? controller) => WindowPlacer.Use(this, key, controller);

    /// <summary>
    /// Shows the window and completes when it closes. Modal over <paramref name="owner"/> when it is on screen, else over
    /// the main window when that is; otherwise on its own (centred, in the taskbar, brought to the front: EasyVocal may be
    /// only in the tray). When a window of the same <see cref="SingleKind"/> is open, that one comes to the front and its
    /// task is returned instead (this one is never shown: <see cref="WasDuplicate"/>).
    /// </summary>
    public Task ShowAsync(Window? owner)
    {
        if (SingleKind is { } kind && single.TryGetValue(kind, out var already))
        {
            WasDuplicate = true;
            if (already.Window.WindowState == WindowState.Minimized) already.Window.WindowState = WindowState.Normal;
            already.Window.Activate();
            return already.Done;
        }
        var done = ShowNow(owner);
        if (SingleKind is { } k) single[k] = (this, done);
        return done;
    }

    private Task ShowNow(Window? owner)
    {
        if (owner is not { IsVisible: true }) owner = MainWindowSource?.Invoke() is { IsVisible: true } main && !ReferenceEquals(main, this) ? main : null;
        if (owner != null)
        {
            // A minimised owner (closing minimises where there is no tray) has no place on screen to centre on, and a
            // modal dialog over it would block an app the person can't see: it comes back first.
            if (owner.WindowState == WindowState.Minimized)
            {
                owner.WindowState = WindowState.Normal;
                owner.Activate();
            }
            WindowStartupLocation = WindowStartupLocation is WindowStartupLocation.Manual ? WindowStartupLocation.Manual : WindowStartupLocation.CenterOwner;
            return ShowDialog(owner);
        }
        if (WindowStartupLocation != WindowStartupLocation.Manual) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true; // the system won't always let a background app take the focus
        var done = new TaskCompletionSource();
        Closed += (_, _) => done.TrySetResult();
        Opened += (_, _) => Topmost = false;
        Show();
        Activate();
        return done.Task;
    }

    /// <summary>One of our text boxes has the keyboard in the active window now (the hotkeys are let go meanwhile).</summary>
    public static bool TextBoxHasKeyboard()
    {
        foreach (var w in open)
            if (w.IsActive && w.FocusManager?.GetFocusedElement() is TextBox { IsEffectivelyVisible: true }) return true;
        return false;
    }

    /// <summary>Closes every window but <paramref name="keep"/> (quitting while a dialog is up), even a busy one.</summary>
    public static void CloseAllExcept(Window? keep)
    {
        foreach (var w in open.ToArray())
            if (!ReferenceEquals(w, keep)) w.CloseForQuit();
        single.Clear();
    }
}
