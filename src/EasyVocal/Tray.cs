using System.Windows;
using EasyVocal.Core.Text;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace EasyVocal;

/// <summary>
/// The notification-area icon: double-click opens the window; the menu has ON/OFF, the visible voices (the active
/// one checked), Open and Quit. WinForms calls back on the UI thread (the WPF dispatcher pumps its messages).
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly AppController controller;
    private readonly Action open;
    private readonly Action quit;
    private readonly Drawing.Icon appIcon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.NotifyIcon icon;
    private Drawing.Font? boldFont;
    private Action? balloonClick;
    private bool disposed;

    public Tray(AppController controller, Action open, Action quit)
    {
        this.controller = controller;
        this.open = open;
        this.quit = quit;
        appIcon = LoadIcon();
        menu = new Forms.ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
        menu.Opening += (_, e) =>
        {
            Rebuild();
            e.Cancel = false;
        };
        Rebuild();
        icon = new Forms.NotifyIcon { Icon = appIcon, Text = "EasyVocal", ContextMenuStrip = menu, Visible = true };
        icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Safe(open);
        };
        icon.BalloonTipClicked += (_, _) =>
        {
            var click = balloonClick;
            balloonClick = null;
            if (click != null) Safe(click);
        };
    }

    /// <summary>The first time the window is closed: say the app keeps running here.</summary>
    public void ShowStillRunningTip()
    {
        if (disposed || controller.Settings.TrayTipShown) return;
        balloonClick = null; // a click on this tip does nothing
        icon.ShowBalloonTip(5000, "EasyVocal", L.T("EasyVocal keeps running here — your voice still works in calls"),
            Forms.ToolTipIcon.Info);
        controller.Settings.TrayTipShown = true;
        controller.Save();
    }

    /// <summary>A balloon whose click runs <paramref name="onClick"/> (e.g. "EasyVocal 0.3.0 is available — click to update").</summary>
    public void ShowBalloon(string text, Action onClick)
    {
        if (disposed) return;
        balloonClick = onClick;
        icon.ShowBalloonTip(10000, "EasyVocal", text, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        appIcon.Dispose();
        boldFont?.Dispose();
    }

    private void Rebuild()
    {
        foreach (Forms.ToolStripItem old in menu.Items.Cast<Forms.ToolStripItem>().ToList()) old.Dispose();
        menu.Items.Clear();

        var onOff = new Forms.ToolStripMenuItem(L.T("Voice filter on")) { Checked = controller.FilterEnabled };
        onOff.Click += (_, _) => Safe(() => controller.FilterEnabled = !controller.FilterEnabled);
        menu.Items.Add(onOff);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var active = controller.ActiveVoice.Id;
        foreach (var v in controller.Settings.Voices)
        {
            if (v.Hidden) continue;
            var id = v.Id;
            var item = new Forms.ToolStripMenuItem(v.Emoji + "  " + AppController.DisplayName(v)) { Checked = id == active };
            item.Click += (_, _) => Safe(() => controller.SelectVoice(id));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());

        var openItem = new Forms.ToolStripMenuItem(L.T("Open EasyVocal"));
        boldFont ??= new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold);
        openItem.Font = boldFont;
        openItem.Click += (_, _) => Safe(open);
        menu.Items.Add(openItem);
        var quitItem = new Forms.ToolStripMenuItem(L.T("Quit"));
        quitItem.Click += (_, _) => Safe(quit);
        menu.Items.Add(quitItem);
    }

    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { AppLog.Error("Tray action failed", ex); }
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/EasyVocal.ico"));
            if (res != null)
            {
                using var stream = res.Stream;
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Tray icon could not be loaded", ex);
        }
        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    /// <summary>The app's dark palette for the tray menu.</summary>
    private sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xE8, 0xE8, 0xEA);
        private static readonly Drawing.Color Accent = Drawing.Color.FromArgb(0x4A, 0xDE, 0x80);

        public DarkRenderer() : base(new DarkColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            var g = e.Graphics;
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Drawing.Pen(Accent, 2f);
            g.DrawLines(pen, new[]
            {
                new Drawing.PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f),
                new Drawing.PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.78f),
                new Drawing.PointF(r.Left + r.Width * 0.82f, r.Top + r.Height * 0.25f),
            });
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var r = e.Item.ContentRectangle;
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(0x3A, 0x40, 0x4C));
            int y = r.Top + r.Height / 2;
            e.Graphics.DrawLine(pen, r.Left + 4, y, r.Right - 4, y);
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Bg2 = Drawing.Color.FromArgb(0x22, 0x26, 0x2E);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x32, 0x38, 0x45);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(0x3A, 0x40, 0x4C);

        public override Drawing.Color ToolStripDropDownBackground => Bg2;
        public override Drawing.Color ImageMarginGradientBegin => Bg2;
        public override Drawing.Color ImageMarginGradientMiddle => Bg2;
        public override Drawing.Color ImageMarginGradientEnd => Bg2;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color CheckBackground => Bg2;
        public override Drawing.Color CheckSelectedBackground => Hover;
        public override Drawing.Color CheckPressedBackground => Hover;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Bg2;
    }
}
