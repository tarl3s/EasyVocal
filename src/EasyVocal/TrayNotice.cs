using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EasyVocal;

/// <summary>
/// A small notice in the corner of the screen next to the tray (bottom right; top right on macOS, under the menu bar):
/// "EasyVocal", the text, and a close ×. A click runs its action (when it has one). It goes away by itself and looks
/// the same on every OS.
/// </summary>
internal sealed class TrayNotice : Window
{
    private static readonly List<TrayNotice> shown = new();
    private readonly DispatcherTimer timer;

    protected override Type StyleKeyOverride => typeof(Window);

    private TrayNotice(string text, Action? onClick, TimeSpan duration)
    {
        Title = "EasyVocal";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Res.Brush("Bg2");
        Foreground = Res.Brush("Text");
        FontFamily = Res.Font("UiFont");
        FontSize = 13;
        if (AppWindow.AppIcon is { } i) Icon = i;

        var close = new Button { Content = "×", Theme = Res.Theme("FlatButton"), Padding = new Thickness(6, 0, 6, 2), FontSize = 16, VerticalAlignment = VerticalAlignment.Top };
        close.Click += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        var title = new TextBlock { Text = "EasyVocal", FontWeight = FontWeight.SemiBold, Foreground = Res.Brush("Accent") };
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Margin = new Thickness(0, 2, 0, 0) };
        var words = new StackPanel { Children = { title, body } };
        var row = new DockPanel { Margin = new Thickness(14, 10, 6, 12) };
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(close);
        row.Children.Add(words);
        Content = new Border { BorderBrush = Res.Brush("Border"), BorderThickness = new Thickness(1), Child = row };
        if (onClick != null)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            PointerPressed += (_, _) =>
            {
                Close();
                try { onClick(); }
                catch (Exception ex) { AppLog.Error("The notice's action failed", ex); }
            };
        }

        timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => Close();
        Opened += (_, _) =>
        {
            PlaceInCorner();
            timer.Start();
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            shown.Remove(this);
        };
    }

    /// <summary>Shows a notice (one at a time: a new one replaces the last).</summary>
    public static void Show(string text, Action? onClick, TimeSpan duration)
    {
        try
        {
            CloseAll();
            var notice = new TrayNotice(text, onClick, duration);
            shown.Add(notice);
            notice.Show();
        }
        catch (Exception ex) { AppLog.Error("The notice could not be shown", ex); }
    }

    /// <summary>The notice on screen now, or null.</summary>
    public static TrayNotice? Shown => shown.Count > 0 ? shown[^1] : null;

    public static void CloseAll()
    {
        foreach (var n in shown.ToArray()) n.Close();
    }

    private void PlaceInCorner()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;
        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(Bounds.Size, screen.Scaling);
        int margin = (int)(12 * screen.Scaling);
        int x = area.Right - size.Width - margin;
        int y = OperatingSystem.IsMacOS() ? area.Y + margin : area.Bottom - size.Height - margin;
        Position = new PixelPoint(x, y);
    }
}
