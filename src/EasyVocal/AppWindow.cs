using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace EasyVocal;

/// <summary>
/// What every EasyVocal window shares: the icon, the theme's window look, a place on screen (<see cref="WindowPlacer"/>),
/// the list of open windows (the tests find dialogs there), a dialog result (<see cref="Accepted"/>), and showing as a
/// dialog over an owner or on its own when EasyVocal is only in the tray.
/// </summary>
public class AppWindow : Window
{
    private static readonly List<AppWindow> open = new();
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
            AppSession.Current?.LookAtTyping();
        };
        Deactivated += (_, _) => AppSession.Current?.LookAtTyping();
    }

    /// <summary>Styled as a plain Window (the theme's "Window" style applies to every subclass).</summary>
    protected override Type StyleKeyOverride => typeof(Window);

    /// <summary>The windows on screen now, oldest first.</summary>
    public static IReadOnlyList<AppWindow> OpenWindows => open.ToArray();

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

    /// <summary>True once the window was closed by its OK / Save / Done (WPF's DialogResult = true).</summary>
    public bool Accepted { get; private set; }

    /// <summary>Closes as accepted.</summary>
    protected void Accept()
    {
        Accepted = true;
        Close();
    }

    /// <summary>A Cancel button: closes, not accepted.</summary>
    protected void CancelClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Keeps the window on screen and where the person left it (<paramref name="controller"/> null = only on screen).</summary>
    protected void Place(string key, AppController? controller) => WindowPlacer.Use(this, key, controller);

    /// <summary>
    /// Shows the window and completes when it closes: a dialog over <paramref name="owner"/> when it is on screen, else
    /// on its own (centred, in the taskbar, brought to the front: EasyVocal may be only in the tray).
    /// </summary>
    public Task ShowAsync(Window? owner)
    {
        if (owner is { IsVisible: true })
        {
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

    /// <summary>Closes every window but <paramref name="keep"/> (quitting while a dialog is up).</summary>
    public static void CloseAllExcept(Window? keep)
    {
        foreach (var w in open.ToArray())
            if (!ReferenceEquals(w, keep)) w.Close();
    }
}
