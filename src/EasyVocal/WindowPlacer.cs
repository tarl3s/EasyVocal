using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using EasyVocal.Core.Model;

namespace EasyVocal;

/// <summary>
/// Keeps every window on the screen and where the person left it. On open a window is never bigger than the
/// monitor's working area (a 1366×768 laptop at 125 % is only ~580 px tall) and never off it; its last place
/// (remembered in <see cref="AppSettings.WindowPlacements"/> under <c>key</c>) comes back when it still fits
/// today's screens. A window that sizes itself to its content only gets its position back, unless the person
/// resized it (Avalonia then turns its SizeToContent off), in which case its size comes back too. The rules are in
/// <see cref="WindowFit"/>. Places are kept in device-independent pixels of the primary screen (as 0.2.0 did).
/// </summary>
public static class WindowPlacer
{
    /// <summary>Call from the window's constructor, after InitializeComponent.</summary>
    public static void Use(Window window, string key, AppController? controller)
    {
        if (controller != null && controller.Settings.WindowPlacements.TryGetValue(key, out var saved) &&
            WindowFit.Fits(saved, WorkAreas(window), window.MinWidth, window.MinHeight))
        {
            double primary = PrimaryScale(window);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint((int)Math.Round(saved.Left * primary), (int)Math.Round(saved.Top * primary));
            if (saved.HasSize)
            {
                // The saved size is the client area: set before the window shows, so it never visibly resizes.
                double own = ScaleAt(window, saved);
                window.SizeToContent = SizeToContent.Manual;
                window.Width = saved.Width * primary / own;
                window.Height = saved.Height * primary / own;
            }
        }
        window.Opened += (_, _) =>
        {
            // Never taller or wider than the screen it opens on: a SizeToContent window then scrolls inside instead.
            var area = AreaFor(window);
            double toOwn = PrimaryScale(window) / OwnScale(window);
            window.MaxWidth = Math.Min(window.MaxWidth, area.Width * toOwn);
            window.MaxHeight = Math.Min(window.MaxHeight, area.Height * toOwn);
            window.MinWidth = Math.Min(window.MinWidth, area.Width * toOwn);
            window.MinHeight = Math.Min(window.MinHeight, area.Height * toOwn);
            KeepOnScreen(window);
        };
        window.Closing += (_, _) => Remember(window, key, controller);
    }

    /// <summary>Notes where the window is now (the app's quit path calls it before the settings are written).</summary>
    public static void Remember(Window window, string key, AppController? controller)
    {
        if (controller == null || !window.IsVisible || window.WindowState != WindowState.Normal) return;
        // Where its frame is, and how big its client area is (what Width and Height set when it comes back).
        var frame = Box(window);
        double scale = OwnScale(window) / PrimaryScale(window);
        var now = new WindowBox(frame.Left, frame.Top, window.ClientSize.Width * scale, window.ClientSize.Height * scale);
        if (!now.IsFinite || now.Width <= 0 || now.Height <= 0) return;
        bool sized = window.SizeToContent == SizeToContent.Manual && window.CanResize;
        var box = sized ? now : new WindowBox(now.Left, now.Top, 0, 0);
        if (controller.Settings.WindowPlacements.TryGetValue(key, out var old) && old == box) return;
        controller.Settings.WindowPlacements[key] = box;
        controller.Save();
    }

    /// <summary>Moves (and if needed shrinks) the window so all of it is on its monitor's working area.</summary>
    public static void KeepOnScreen(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;
        var area = AreaFor(window);
        var now = Box(window);
        if (!now.IsFinite) return;
        var fit = WindowFit.Fit(now, area);
        double primary = PrimaryScale(window), toOwn = primary / OwnScale(window);
        if (fit.Width < now.Width - 0.5 || fit.Height < now.Height - 0.5)
        {
            // Width and Height are the client area: take off what the frame adds around it.
            var frame = window.FrameSize ?? window.ClientSize;
            double extraW = Math.Max(0, frame.Width - window.ClientSize.Width), extraH = Math.Max(0, frame.Height - window.ClientSize.Height);
            window.SizeToContent = SizeToContent.Manual;
            window.Width = Math.Max(1, fit.Width * toOwn - extraW);
            window.Height = Math.Max(1, fit.Height * toOwn - extraH);
        }
        if (Math.Abs(fit.Left - now.Left) > 0.5 || Math.Abs(fit.Top - now.Top) > 0.5)
            window.Position = new PixelPoint((int)Math.Round(fit.Left * primary), (int)Math.Round(fit.Top * primary));
    }

    /// <summary>The window's place now (frame included where the system says), in primary-screen DIPs.</summary>
    public static WindowBox Box(Window window)
    {
        double primary = PrimaryScale(window), own = OwnScale(window);
        var size = window.FrameSize ?? window.ClientSize;
        if (size.Width <= 0 || size.Height <= 0) size = new Size(window.Width, window.Height);
        return new WindowBox(window.Position.X / primary, window.Position.Y / primary, size.Width * own / primary, size.Height * own / primary);
    }

    /// <summary>The working area (screen minus taskbar or dock) of the monitor the window is on.</summary>
    public static WindowBox AreaFor(Window window)
    {
        var areas = WorkAreas(window);
        var anchor = window.Owner is Window { IsVisible: true } owner && !window.IsVisible ? Box(owner) : Box(window);
        if (!anchor.IsFinite) return areas[0]; // the primary screen
        return WindowFit.AreaFor(new WindowBox(anchor.Left, anchor.Top, Math.Max(1, anchor.Width), Math.Max(1, anchor.Height)), areas);
    }

    /// <summary>Every monitor's working area in primary-screen DIPs, the primary first (a stand-in 1280×720 when none is known).</summary>
    public static IReadOnlyList<WindowBox> WorkAreas(TopLevel window)
    {
        var list = new List<WindowBox>();
        try
        {
            var screens = window.Screens;
            if (screens != null)
            {
                double primary = PrimaryScale(window);
                foreach (var s in screens.All.OrderByDescending(s => s.IsPrimary))
                {
                    var a = s.WorkingArea.Width > 0 ? s.WorkingArea : s.Bounds;
                    list.Add(new WindowBox(a.X / primary, a.Y / primary, a.Width / primary, a.Height / primary));
                }
            }
        }
        catch (Exception) { /* fall back below */ }
        if (list.Count == 0) list.Add(new WindowBox(0, 0, 1280, 720));
        return list;
    }

    private static double PrimaryScale(TopLevel window)
    {
        try
        {
            var p = window.Screens?.Primary ?? window.Screens?.All.FirstOrDefault();
            if (p != null && p.Scaling > 0 && double.IsFinite(p.Scaling)) return p.Scaling;
        }
        catch (Exception) { }
        return 1;
    }

    private static double OwnScale(Window window)
    {
        double s = window.DesktopScaling;
        return s > 0 && double.IsFinite(s) ? s : PrimaryScale(window);
    }

    /// <summary>The scaling of the screen a remembered box lies on.</summary>
    private static double ScaleAt(Window window, WindowBox box)
    {
        try
        {
            double primary = PrimaryScale(window);
            var screen = window.Screens?.ScreenFromPoint(new PixelPoint((int)((box.Left + 20) * primary), (int)((box.Top + 20) * primary)));
            if (screen != null && screen.Scaling > 0) return screen.Scaling;
        }
        catch (Exception) { }
        return PrimaryScale(window);
    }
}
