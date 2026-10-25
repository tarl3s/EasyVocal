using System.Windows;
using EasyVocal.Core.Model;

namespace EasyVocal;

/// <summary>
/// Keeps every window on the screen and where the person left it. On open a window is never bigger than the
/// monitor's working area (a 1366×768 laptop at 125 % is only ~580 px tall) and never off it; its last place
/// (remembered in <see cref="AppSettings.WindowPlacements"/> under <c>key</c>) comes back when it still fits
/// today's screens. A window that sizes itself to its content only gets its position back, unless the person
/// resized it (WPF then turns its SizeToContent off), in which case its size comes back too. The rules are in
/// <see cref="WindowFit"/>.
/// </summary>
public static class WindowPlacer
{
    /// <summary>Call from the window's constructor, after InitializeComponent.</summary>
    public static void Use(Window window, string key, AppController? controller)
    {
        var areas = WorkAreas();
        if (controller != null && controller.Settings.WindowPlacements.TryGetValue(key, out var saved) &&
            WindowFit.Fits(saved, areas, window.MinWidth, window.MinHeight))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = saved.Left;
            window.Top = saved.Top;
            if (saved.HasSize)
            {
                window.SizeToContent = SizeToContent.Manual;
                window.Width = saved.Width;
                window.Height = saved.Height;
            }
        }

        window.SourceInitialized += (_, _) =>
        {
            // Never taller or wider than the screen it opens on: a SizeToContent window then scrolls inside instead.
            var area = AreaFor(window);
            window.MaxWidth = Math.Min(window.MaxWidth, area.Width);
            window.MaxHeight = Math.Min(window.MaxHeight, area.Height);
            window.MinWidth = Math.Min(window.MinWidth, area.Width);
            window.MinHeight = Math.Min(window.MinHeight, area.Height);
        };
        window.Loaded += (_, _) => KeepOnScreen(window);
        window.Closing += (_, e) => Remember(window, key, controller);
    }

    /// <summary>Notes where the window is now (the app's quit path calls it before the settings are written).</summary>
    public static void Remember(Window window, string key, AppController? controller)
    {
        if (controller == null || !window.IsLoaded) return;
        var r = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        if (r.IsEmpty || !double.IsFinite(r.Left) || !double.IsFinite(r.Top)) return;
        bool sized = window.SizeToContent == SizeToContent.Manual && window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
        var box = sized ? new WindowBox(r.Left, r.Top, r.Width, r.Height) : new WindowBox(r.Left, r.Top, 0, 0);
        if (controller.Settings.WindowPlacements.TryGetValue(key, out var old) && old == box) return;
        controller.Settings.WindowPlacements[key] = box;
        controller.Save();
    }

    /// <summary>Moves (and if needed shrinks) the window so all of it is on its monitor's working area.</summary>
    public static void KeepOnScreen(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;
        var area = AreaFor(window);
        var now = new WindowBox(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        if (!now.IsFinite) return;
        var fit = WindowFit.Fit(now, area);
        if (fit.Width < now.Width - 0.5 || fit.Height < now.Height - 0.5)
        {
            window.SizeToContent = SizeToContent.Manual;
            window.Width = fit.Width;
            window.Height = fit.Height;
        }
        if (Math.Abs(fit.Left - now.Left) > 0.5) window.Left = fit.Left;
        if (Math.Abs(fit.Top - now.Top) > 0.5) window.Top = fit.Top;
    }

    /// <summary>The working area (screen minus taskbar) of the monitor the window is on, in WPF units.</summary>
    public static WindowBox AreaFor(Window window)
    {
        var areas = WorkAreas();
        var anchor = window.Owner is { IsLoaded: true } owner && !window.IsLoaded
            ? new WindowBox(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight)
            : new WindowBox(window.Left, window.Top, Math.Max(1, window.ActualWidth), Math.Max(1, window.ActualHeight));
        if (!anchor.IsFinite) return areas[0]; // the primary screen
        return WindowFit.AreaFor(anchor, areas);
    }

    /// <summary>
    /// Every monitor's working area in WPF units, the primary first. EasyVocal is system-DPI aware, so one scale
    /// (the primary's pixels per WPF unit) converts them all.
    /// </summary>
    public static IReadOnlyList<WindowBox> WorkAreas()
    {
        var work = SystemParameters.WorkArea;
        var list = new List<WindowBox>();
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var primary = System.Windows.Forms.Screen.PrimaryScreen;
            double scale = primary != null && primary.WorkingArea.Width > 0 ? primary.WorkingArea.Width / work.Width : 1;
            if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
            foreach (var s in screens.OrderByDescending(s => s.Primary))
            {
                var a = s.WorkingArea;
                list.Add(new WindowBox(a.Left / scale, a.Top / scale, a.Width / scale, a.Height / scale));
            }
        }
        catch (Exception) { /* fall back to the primary screen below */ }
        if (list.Count == 0) list.Add(new WindowBox(work.Left, work.Top, work.Width, work.Height));
        return list;
    }
}
