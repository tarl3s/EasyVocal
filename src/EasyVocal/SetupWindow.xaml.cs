using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EasyVocal.Audio;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// First run (and the banner's "Fix" button): 1. the virtual microphone (VB-CABLE) with a one-click install,
/// 2. which real microphone to use, with a live level bar, 3. how to choose the virtual microphone in Discord,
/// WhatsApp or any other app (drawn here, no screenshots), and "Start with Windows". "Done" marks the setup as done.
/// </summary>
public partial class SetupWindow : Window
{
    /// <summary>The recording end of VB-CABLE as Windows names it: what a call app must pick as its microphone.</summary>
    public const string CableOutputName = "CABLE Output (VB-Audio Virtual Cable)";

    private static readonly TimeSpan WaitForDevice = TimeSpan.FromSeconds(60);

    private readonly AppController controller;
    private readonly Func<bool> cableProbe;
    private readonly DispatcherTimer meterTimer, waitTimer;
    private MicMeter? meter;
    private CancellationTokenSource? cancelInstall;
    private DateTime waitUntil;
    private string micFingerprint = "";
    private double meterShown, progressValue;
    private bool loadingMics, installing, waiting, cableWasReady, closed;

    /// <param name="cableProbe">Tells whether the virtual microphone is installed; null = look at the Windows devices (tests pass their own).</param>
    public SetupWindow(AppController controller, Func<bool>? cableProbe = null)
    {
        this.controller = controller;
        this.cableProbe = cableProbe ?? (() => DeviceRules.FindVirtualCable(DeviceList.Outputs()) != null);
        InitializeComponent();
        DarkTitleBar.Use(this);
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40);

        BuildGuides();
        StartBox.IsChecked = controller.Settings.SetupDone ? controller.Settings.StartWithWindows : true;

        meterTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        meterTimer.Tick += (_, _) => UpdateMeter();
        waitTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        waitTimer.Tick += (_, _) => WaitTick();

        cableWasReady = this.cableProbe(); // already there when the window opened: no audio restart needed
        RefreshCable();
        FillMics();
        DeviceList.Changed += OnDevicesChanged;
        meterTimer.Start();
    }

    /// <summary>Shows the window and waits for it. True when the person pressed Done.</summary>
    public static bool Open(AppController controller, Window? owner)
    {
        var window = new SetupWindow(controller);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true;
    }

    // ---------- 1. virtual microphone ----------

    /// <summary>Shows the ready tick or the Install button for what Windows has now.</summary>
    private bool RefreshCable()
    {
        bool ready = cableProbe();
        CableReadyPanel.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        CableMissingPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        if (!ready) return false;
        waiting = false;
        waitTimer.Stop();
        if (!cableWasReady)
        {
            cableWasReady = true;
            controller.RestartAudio(); // the voice starts flowing to the new microphone right away
        }
        return true;
    }

    internal void SetStatus(string text, bool problem)
    {
        CableStatus.Text = text;
        CableStatus.Foreground = (Brush)FindResource(problem ? "Warn" : "Text");
        CableStatus.Visibility = Visibility.Visible;
    }

    private void SetProgress(double value)
    {
        progressValue = Math.Clamp(value, 0, 1);
        ProgressFill.Width = ProgressTrack.ActualWidth * progressValue;
    }

    private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e) => SetProgress(progressValue);

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (installing) return;
        installing = true;
        waiting = false;
        waitTimer.Stop();
        InstallButton.IsEnabled = false;
        ProgressTrack.Visibility = Visibility.Visible;
        SetProgress(0);
        SetStatus(L.T("Downloading VB-CABLE…"), false);
        cancelInstall = new CancellationTokenSource();
        bool ok;
        try
        {
            ok = await VbCableInstaller.DownloadAndRunAsync(new Progress<double>(OnInstallProgress), cancelInstall.Token);
        }
        finally
        {
            installing = false;
        }
        if (closed) return;

        InstallButton.IsEnabled = true;
        if (!ok)
        {
            ProgressTrack.Visibility = Visibility.Collapsed;
            if (VbCableInstaller.UserCancelled)
            {
                SetStatus(L.T("The installation was cancelled. Press Install to try again."), true);
                return;
            }
            AppLog.Info("VB-CABLE install failed: " + VbCableInstaller.LastError);
            ShowOfficialPageFallback();
            return;
        }

        if (RefreshCable()) return;
        // The installer closed but Windows may take a moment to list the new devices (or needs a restart).
        waiting = true;
        waitUntil = DateTime.UtcNow + WaitForDevice;
        SetProgress(1);
        SetStatus(L.T("Waiting for Windows to finish installing…"), false);
        waitTimer.Start();
    }

    private void OnInstallProgress(double value)
    {
        SetProgress(value);
        if (value >= 1) SetStatus(L.T("Allow the installer to make changes, then click Install Driver in its window."), false);
    }

    /// <summary>The download didn't work: open the official page and say what to do there.</summary>
    internal void ShowOfficialPageFallback(bool open = true)
    {
        SetStatus(L.T("Download it from the official page, run VBCABLE_Setup_x64.exe, then come back."), true);
        if (open) VbCableInstaller.OpenOfficialPage();
    }

    /// <summary>Once a second while waiting for the new devices: ready, or give up after a minute.</summary>
    private void WaitTick()
    {
        RefreshCable();
        if (!waiting) return;
        if (DateTime.UtcNow < waitUntil) return;
        waiting = false;
        waitTimer.Stop();
        ProgressTrack.Visibility = Visibility.Collapsed;
        ShowRestartMessage();
    }

    internal void ShowRestartMessage() =>
        SetStatus(L.T("Restart your PC to finish installing, then open EasyVocal again."), true);

    // Windows audio thread.
    private void OnDevicesChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        RefreshCable();
        FillMics();
    });

    // ---------- 2. microphone ----------

    /// <summary>Lists the real microphones (VB-CABLE's own end is never offered) and keeps the choice; restarts the level bar.</summary>
    private void FillMics()
    {
        var inputs = DeviceList.Inputs();
        var mics = DeviceRules.ExcludeVirtualCable(inputs);
        var fingerprint = string.Join("|", mics.Select(m => m.Id)) + "#" + controller.Settings.MicDeviceId;
        if (fingerprint == micFingerprint) return;
        micFingerprint = fingerprint;

        loadingMics = true;
        try
        {
            MicBox.Items.Clear();
            var auto = new ComboBoxItem { Content = L.T("Automatic (Windows default)"), Tag = null };
            MicBox.Items.Add(auto);
            ComboBoxItem selected = auto;
            foreach (var mic in mics)
            {
                var item = new ComboBoxItem { Content = mic.Name, Tag = mic };
                MicBox.Items.Add(item);
                if (mic.Id == controller.Settings.MicDeviceId) selected = item;
            }
            MicBox.SelectedItem = selected;
            NoMicText.Visibility = mics.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            loadingMics = false;
        }
        StartMeter(inputs);
    }

    private void StartMeter(IReadOnlyList<DeviceInfo> inputs)
    {
        meter?.Dispose();
        meter = null;
        var id = (MicBox.SelectedItem as ComboBoxItem)?.Tag is DeviceInfo mic ? mic.Id : DeviceRules.DefaultMic(inputs)?.Id;
        if (id != null) meter = new MicMeter(id);
    }

    private void MicBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingMics || MicBox.SelectedItem is not ComboBoxItem item) return;
        var id = (item.Tag as DeviceInfo)?.Id;
        if (id == controller.Settings.MicDeviceId) return;
        controller.Settings.MicDeviceId = id;
        controller.Save();
        controller.RestartAudio();
        micFingerprint = ""; // the next list refresh must see the new choice
        StartMeter(DeviceList.Inputs());
    }

    private void UpdateMeter()
    {
        if (meter == null)
        {
            meterShown = 0;
        }
        else
        {
            double fraction = MicMeter.BarFraction(meter.TakeLevel());
            meterShown = Math.Max(fraction, meterShown - 0.04);
        }
        MeterBar.Width = Math.Max(0, MeterTrack.ActualWidth * meterShown);
    }

    // ---------- 3. the per-app guide (drawn, not screenshots) ----------

    private void BuildGuides()
    {
        AddGuide(L.T("Discord"),
            L.T("Input Device"), L.T("Settings → Voice & Video → Input Device →"),
            L.T("Press “Let’s Check” there to try it: you hear your changed voice."));
        AddGuide(L.T("WhatsApp"),
            L.T("Microphone"), L.T("Settings → Voice & video → Microphone →"),
            L.T("Start a call to try it: the other person hears your changed voice."));
        AddGuide(L.T("Other"),
            L.T("Microphone"), L.T("In the app's sound or voice settings, set the microphone to"),
            L.T("Keep EasyVocal open while you talk: the voice only reaches the call while it runs."));
        GuideTabs.SelectedIndex = 0;
    }

    private void AddGuide(string tabName, string rowLabel, string path, string tip)
    {
        var res = Application.Current.Resources;
        var mock = new Border
        {
            Background = (Brush)res["Bg2"],
            BorderBrush = (Brush)res["Border"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
        };
        var mockContent = new StackPanel();
        mockContent.Children.Add(new TextBlock
        {
            Text = rowLabel,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)res["TextDim"],
            Margin = new Thickness(0, 0, 0, 6),
        });
        var fakeBox = new Border
        {
            Background = (Brush)res["Bg0"],
            BorderBrush = (Brush)res["Accent"],
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
        };
        var fakeRow = new Grid();
        fakeRow.Children.Add(new TextBlock { Text = CableOutputName, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 24, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        fakeRow.Children.Add(new Path
        {
            Data = Geometry.Parse("M0,0 L4,4 L8,0"),
            Stroke = (Brush)res["TextDim"],
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });
        fakeBox.Child = fakeRow;
        mockContent.Children.Add(fakeBox);
        mock.Child = mockContent;

        var line1 = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        line1.Inlines.Add(new Run(path + " "));
        line1.Inlines.Add(new Run(CableOutputName) { FontWeight = FontWeights.Bold, Foreground = (Brush)res["Accent"] });
        var line2 = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = (Brush)res["TextDim"] };

        var panel = new StackPanel();
        panel.Children.Add(mock);
        panel.Children.Add(line1);
        panel.Children.Add(line2);
        // The selected tab sets a bold dark-on-green look that its content would inherit.
        TextElement.SetForeground(panel, (Brush)res["Text"]);
        TextElement.SetFontWeight(panel, FontWeights.Normal);

        GuideTabs.Items.Add(new TabItem { Header = tabName, Content = panel });
    }

    // ---------- done ----------

    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        controller.Settings.SetupDone = true;
        if (!controller.SetStartWithWindows(StartBox.IsChecked == true))
            AskDialog.Tell(this, L.T("Windows would not let EasyVocal start with it. You can try again in Settings."));
        controller.Save();
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        DeviceList.Changed -= OnDevicesChanged;
        meterTimer.Stop();
        waitTimer.Stop();
        cancelInstall?.Cancel();
        meter?.Dispose();
        meter = null;
        base.OnClosed(e);
    }
}
