using Avalonia;
using Avalonia.Controls;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The notification-area (menu bar on macOS) icon: a click opens the window; the menu has ON/OFF, the visible voices
/// (the active one checked), Open and Quit. Messages that used to be tray balloons are a small notice window of our own
/// (<see cref="TrayNotice"/>), the same on every OS.
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly AppController controller;
    private readonly Action open;
    private readonly Action quit;
    private readonly TrayIcon icon;
    private readonly NativeMenu menu = new();
    private string menuKey = "";
    private bool disposed;

    public Tray(AppController controller, Action open, Action quit)
    {
        this.controller = controller;
        this.open = open;
        this.quit = quit;
        Rebuild();
        icon = new TrayIcon { ToolTipText = "EasyVocal", Menu = menu, IsVisible = true };
        if (AppWindow.AppIcon is { } i) icon.Icon = i;
        icon.Clicked += (_, _) => Safe(open);
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { icon });
        controller.Changed += Rebuild;
    }

    /// <summary>The first time the window is closed: say the app keeps running here.</summary>
    public void ShowStillRunningTip()
    {
        if (disposed || controller.Settings.TrayTipShown) return;
        TrayNotice.Show(L.T("EasyVocal keeps running here — your voice still works in calls"), null, TimeSpan.FromSeconds(6));
        controller.Settings.TrayTipShown = true;
        controller.Save();
    }

    /// <summary>A notice whose click runs <paramref name="onClick"/> (e.g. "EasyVocal 0.3.0 is available — click to update").</summary>
    public void ShowBalloon(string text, Action onClick)
    {
        if (disposed) return;
        TrayNotice.Show(text, onClick, TimeSpan.FromSeconds(12));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        controller.Changed -= Rebuild;
        TrayNotice.CloseAll();
        icon.IsVisible = false;
        TrayIcon.SetIcons(Application.Current!, new TrayIcons());
        icon.Dispose();
    }

    /// <summary>Rebuilds the menu when what it shows changed (ON/OFF, the voices, the active one, the language).</summary>
    private void Rebuild()
    {
        if (disposed) return;
        var active = controller.ActiveVoice.Id;
        var key = controller.FilterEnabled + "|" + active + "|" + L.Language + "|" +
                  string.Join(",", controller.Settings.Voices.Where(v => !v.Hidden).Select(v => v.Id + ":" + v.Name + ":" + v.Emoji));
        if (key == menuKey) return;
        menuKey = key;
        menu.Items.Clear();

        var onOff = new NativeMenuItem(L.T("Voice filter on")) { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = controller.FilterEnabled };
        onOff.Click += (_, _) => Safe(() => controller.FilterEnabled = !controller.FilterEnabled);
        menu.Items.Add(onOff);
        menu.Items.Add(new NativeMenuItemSeparator());

        foreach (var v in controller.Settings.Voices)
        {
            if (v.Hidden) continue;
            var id = v.Id;
            var item = new NativeMenuItem(v.Emoji + "  " + AppController.DisplayName(v)) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = id == active };
            item.Click += (_, _) => Safe(() => controller.SelectVoice(id));
            menu.Items.Add(item);
        }
        menu.Items.Add(new NativeMenuItemSeparator());

        var openItem = new NativeMenuItem(L.T("Open EasyVocal"));
        openItem.Click += (_, _) => Safe(open);
        menu.Items.Add(openItem);
        var quitItem = new NativeMenuItem(L.T("Quit"));
        quitItem.Click += (_, _) => Safe(quit);
        menu.Items.Add(quitItem);
    }

    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { AppLog.Error("Tray action failed", ex); }
    }
}
