using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// First run (and the banner's "Fix" button): 1. the virtual microphone (VB-CABLE on Windows) with a one-click install,
/// 2. which real microphone to use, with a live level bar, 3. how to choose the virtual microphone in Discord,
/// WhatsApp or any other app (drawn here, no screenshots), and "Start with Windows". "Done" marks the setup as done.
/// </summary>
public partial class SetupWindow : AppWindow
{
    /// <summary>What a call app must pick as its microphone (VB-CABLE's recording end on Windows).</summary>
    private string CallAppDevice => controller.Platform.VirtualMic.CallAppDeviceName;

    /// <summary>The setup's sentences about this platform's virtual microphone.</summary>
    private VirtualMicTexts MicTexts => controller.Platform.VirtualMic.Texts;

    private static readonly TimeSpan WaitForDevice = TimeSpan.FromSeconds(60);

    private readonly AppController controller;
    private readonly Func<bool> cableProbe;
    private readonly DispatcherTimer meterTimer, waitTimer;
    private MicMeter? meter;
    private string? meterMicId; // the microphone the level bar shows
    private bool meterFromEngine;
    private CancellationTokenSource? cancelInstall;
    private DateTime waitUntil;
    private string micFingerprint = "";
    private double meterShown, progressValue;
    private bool loadingMics, installing, waiting, cableWasReady, closed;
    private static bool installingAnywhere;

    /// <summary>One setup at a time (a second one brings the open one to the front).</summary>
    protected override string? SingleKind => "setup";

    /// <param name="cableProbe">Tells whether the virtual microphone is installed; null = look at the system's devices (tests pass their own).</param>
    public SetupWindow(AppController controller, Func<bool>? cableProbe = null)
    {
        this.controller = controller;
        this.cableProbe = cableProbe ?? (() => controller.Platform.VirtualMic.Find(controller.Devices.Outputs()) != null);
        InitializeComponent();
        StartBox.Content = L.T(controller.Platform.Texts.StartAtLoginRecommended);
        CableExplanation.Text = L.T(MicTexts.Explanation);
        InstallButton.IsVisible = controller.Platform.VirtualMic.CanInstall;
        Place("setup", controller);

        BuildGuides();
        StartBox.IsChecked = controller.Settings.SetupDone ? controller.Settings.StartWithWindows : true;
        ProgressTrack.SizeChanged += (_, _) => SetProgress(progressValue);

        meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        meterTimer.Tick += (_, _) => UpdateMeter();
        waitTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        waitTimer.Tick += (_, _) => WaitTick();

        cableWasReady = this.cableProbe(); // already there when the window opened: no audio restart needed
        RefreshCable();
        // What listens (the level bar opens the mic, device changes, timers) starts only once the window is on screen: a
        // setup that is never shown (one is open already) leaves nothing running.
        Opened += (_, _) =>
        {
            FillMics();
            controller.Devices.Changed += OnDevicesChanged;
            controller.Changed += OnControllerChanged;
            meterTimer.Start();
        };
    }

    /// <summary>Shows the window and waits for it. True when the person pressed Done.</summary>
    public static async Task<bool> OpenAsync(AppController controller, Window? owner)
    {
        var window = new SetupWindow(controller);
        await window.ShowAsync(owner);
        return window.Accepted;
    }

    // ---------- 1. virtual microphone ----------

    /// <summary>Shows the ready tick or the Install button for what the system has now.</summary>
    private bool RefreshCable()
    {
        bool ready = cableProbe();
        CableReadyPanel.IsVisible = ready;
        CableMissingPanel.IsVisible = !ready;
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
        CableStatus.Foreground = Res.Brush(problem ? "Warn" : "Text");
        CableStatus.IsVisible = true;
    }

    private void SetProgress(double value)
    {
        progressValue = Math.Clamp(value, 0, 1);
        ProgressFill.Width = ProgressTrack.Bounds.Width * progressValue;
    }

    private async void InstallButton_Click(object? sender, RoutedEventArgs e)
    {
        if (installing || installingAnywhere) return; // never two installers at once
        installing = installingAnywhere = true;
        InstallResult result;
        try
        {
            // Everything from here on is inside the try: a throw must never leave the "an installer runs" guard set.
            waiting = false;
            waitTimer.Stop();
            InstallButton.IsEnabled = false;
            DoneButton.IsEnabled = false; // finishing now would leave the installer running behind a closed window
            ProgressTrack.IsVisible = true;
            SetProgress(0);
            SetStatus(L.T(MicTexts.Downloading), false);
            cancelInstall = new CancellationTokenSource();
            // VB-CABLE may make itself the system's default playback device and microphone: what the person has now is put back.
            controller.RememberDefaultsBeforeCable();
            result = await controller.CableInstaller(new Progress<double>(OnInstallProgress), cancelInstall.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error("The virtual microphone's install failed", ex);
            result = new InstallResult(InstallStatus.Failed, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            installing = installingAnywhere = false;
        }
        if (controller.IsDisposed) return; // EasyVocal quit while the installer ran
        // Cancelled = the window was closed: an installer already running may still add the cable (the controller watches).
        if (result.Status is InstallStatus.DeclinedPermission or InstallStatus.NotVerified or InstallStatus.Failed)
            controller.ForgetDefaultsBeforeCable();
        if (closed) return;

        InstallButton.IsEnabled = true;
        DoneButton.IsEnabled = true;
        if (result.Status != InstallStatus.Installed)
        {
            ProgressTrack.IsVisible = false;
            switch (result.Status)
            {
                case InstallStatus.DeclinedPermission:
                    SetStatus(L.T("The installation was cancelled. Press Install to try again."), true);
                    break;
                case InstallStatus.Cancelled:
                    break;
                case InstallStatus.NotVerified:
                    AppLog.Info("Virtual mic installer not run: " + result.Detail);
                    ShowNotVerifiedFallback();
                    break;
                default:
                    AppLog.Info("Virtual mic install failed: " + result.Detail);
                    ShowOfficialPageFallback();
                    break;
            }
            return;
        }

        if (RefreshCable()) return;
        // The installer closed but the system may take a moment to list the new devices (or needs a restart).
        waiting = true;
        waitUntil = DateTime.UtcNow + WaitForDevice;
        SetProgress(1);
        SetStatus(L.T(controller.Platform.Texts.WaitingForInstall), false);
        waitTimer.Start();
    }

    private void OnInstallProgress(double value)
    {
        if (closed) return;
        SetProgress(value);
        if (value >= 1) SetStatus(L.T(MicTexts.InstallerPrompt), false);
    }

    /// <summary>The download didn't work: open the official page and say what to do there.</summary>
    internal void ShowOfficialPageFallback(bool open = true)
    {
        SetStatus(L.T(MicTexts.InstallManually), true);
        if (open) controller.Platform.VirtualMic.OpenPage();
    }

    /// <summary>The downloaded installer isn't signed by its maker: it was not run. Open the official page and say so.</summary>
    internal void ShowNotVerifiedFallback(bool open = true)
    {
        SetStatus(L.T(MicTexts.NotVerified), true);
        if (open) controller.Platform.VirtualMic.OpenPage();
    }

    /// <summary>Once a second while waiting for the new devices: ready, or give up after a minute.</summary>
    private void WaitTick()
    {
        RefreshCable();
        if (!waiting) return;
        if (DateTime.UtcNow < waitUntil) return;
        waiting = false;
        waitTimer.Stop();
        ProgressTrack.IsVisible = false;
        ShowRestartMessage();
    }

    internal void ShowRestartMessage() =>
        SetStatus(L.T(controller.Platform.Texts.RestartToFinish), true);

    // The system's audio thread.
    private void OnDevicesChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (closed) return;
        RefreshCable();
        FillMics();
    });

    // ---------- 2. microphone ----------

    /// <summary>Lists the real microphones (the virtual mic's own end is never offered) and keeps the choice; restarts the level bar.</summary>
    private void FillMics()
    {
        var inputs = controller.Devices.Inputs();
        var mics = DeviceRules.ExcludeVirtualCable(controller.Platform.VirtualMic.Cable, inputs);
        var fingerprint = string.Join("|", mics.Select(m => m.Id)) + "#" + controller.Settings.MicDeviceId;
        if (fingerprint == micFingerprint) return;
        micFingerprint = fingerprint;

        loadingMics = true;
        try
        {
            // The same list as Settings: a chosen mic that is unplugged shows as "Device not connected", not "Automatic".
            DeviceBox.Fill(MicBox, L.T(controller.Platform.Texts.AutomaticMic), mics, controller.Settings.MicDeviceId);
            NoMicText.IsVisible = mics.Count == 0;
        }
        finally
        {
            loadingMics = false;
        }
        StartMeter(inputs);
    }

    private void StartMeter(IReadOnlyList<DeviceInfo> inputs)
    {
        meterMicId = DeviceBox.IdOf(MicBox) ?? DeviceRules.DefaultMic(controller.Platform.VirtualMic.Cable, inputs)?.Id;
        PickMeterSource(restart: true);
    }

    /// <summary>
    /// The audio has (or opens again by itself) the microphone the bar shows: the bar shows the audio's level, and never
    /// opens a second stream on that device (opening and closing one beside the voice's stream could freeze CoreAudio on
    /// a Mac). Otherwise the bar listens on its own. Asked again whenever the audio or the devices change.
    /// </summary>
    private void PickMeterSource(bool restart)
    {
        bool fromEngine = meterMicId != null && meterMicId == controller.AudioMicId;
        if (!restart && fromEngine == meterFromEngine) return;
        meter?.Dispose();
        meter = null;
        meterFromEngine = fromEngine;
        if (!fromEngine && meterMicId != null && !closed)
            meter = new MicMeter(controller.Devices, meterMicId); // an unplugged choice can't be opened: the bar just stays empty
    }

    private void OnControllerChanged()
    {
        if (!closed) PickMeterSource(restart: false);
    }

    /// <summary>The level bar listens through the audio (for the tests).</summary>
    internal bool MeterFromEngine => meterFromEngine;

    private void MicBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMics || MicBox.SelectedItem is not ComboBoxItem) return;
        var id = DeviceBox.IdOf(MicBox);
        if (id == controller.Settings.MicDeviceId) return;
        controller.Settings.MicDeviceId = id;
        controller.Save();
        controller.RestartAudio();
        micFingerprint = ""; // the next list refresh must see the new choice
        StartMeter(controller.Devices.Inputs());
    }

    private void UpdateMeter()
    {
        float level = meterFromEngine ? controller.InputLevel : meter?.TakeLevel() ?? 0;
        meterShown = !meterFromEngine && meter == null ? 0 : MicMeter.NextFill(meterShown, level);
        MeterBar.Width = Math.Max(0, MeterTrack.Bounds.Width * meterShown);
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
        var mock = new Border
        {
            Background = Res.Brush("Bg2"),
            BorderBrush = Res.Brush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
        };
        var mockContent = new StackPanel();
        mockContent.Children.Add(new TextBlock
        {
            Text = rowLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Res.Brush("TextDim"),
            Margin = new Thickness(0, 0, 0, 6),
        });
        var fakeBox = new Border
        {
            Background = Res.Brush("Bg0"),
            BorderBrush = Res.Brush("Accent"),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
        };
        var fakeRow = new Panel();
        fakeRow.Children.Add(new TextBlock { Text = CallAppDevice, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 24, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        fakeRow.Children.Add(new Path
        {
            Data = Geometry.Parse("M0,0 L4,4 L8,0"),
            Stroke = Res.Brush("TextDim"),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });
        fakeBox.Child = fakeRow;
        mockContent.Children.Add(fakeBox);
        mock.Child = mockContent;

        var line1 = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        line1.Inlines = new InlineCollection
        {
            new Run(path + " "),
            new Run(CallAppDevice) { FontWeight = FontWeight.Bold, Foreground = Res.Brush("Accent") },
        };
        var line2 = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = Res.Brush("TextDim") };

        var panel = new StackPanel();
        panel.Children.Add(mock);
        panel.Children.Add(line1);
        panel.Children.Add(line2);
        // The selected tab sets a bold dark-on-green look that its content would inherit.
        TextElement.SetForeground(panel, Res.Brush("Text"));
        TextElement.SetFontWeight(panel, FontWeight.Normal);
        TextElement.SetFontSize(panel, 13);

        GuideTabs.Items.Add(new TabItem { Header = tabName, Content = panel });
    }

    // ---------- done ----------

    private async void DoneButton_Click(object? sender, RoutedEventArgs e)
    {
        controller.Settings.SetupDone = true;
        if (!controller.SetStartWithWindows(StartBox.IsChecked == true))
            await AskDialog.TellAsync(this, L.T(controller.Platform.Texts.StartAtLoginRefusedTryAgain));
        controller.Save();
        Accept();
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        controller.Devices.Changed -= OnDevicesChanged;
        controller.Changed -= OnControllerChanged;
        meterTimer.Stop();
        waitTimer.Stop();
        cancelInstall?.Cancel();
        meter?.Dispose();
        meter = null;
        base.OnClosed(e);
    }
}
