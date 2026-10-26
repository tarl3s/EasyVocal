using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EasyVocal.Ai.Store;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The Voices tab's "🧠 AI voices" section: without the AI pack a card to download it (progress, cancel, a stopped
/// download goes on next time, then the PC is tested once); with it the AI voice cards (🧠 badge, a spinner while one
/// loads) and "＋ Add AI voice". The top bar shows how late the AI voice reaches the call.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<string> aiCardIds = new();
    private readonly Dictionary<string, Control> aiSpinners = new();
    private Border? downloadCard;
    private Button? downloadButton, downloadCancel;
    private Border? downloadTrack, downloadFill;
    private TextBlock? downloadStatus;
    private CancellationTokenSource? downloadCts;
    private bool downloading, testing;

    /// <summary>For the tests: the download card's button, the "＋ Add AI voice" card, the download's status line.</summary>
    internal Button? DownloadAiButton => downloadButton;
    internal Button? DownloadAiCancel => downloadCancel;
    internal TextBlock? DownloadAiStatus => downloadStatus;
    internal Button? AddAiVoiceCard { get; private set; }
    internal bool AiTesting => testing;

    /// <summary>Every voice card on screen (classic and AI).</summary>
    internal IReadOnlyCollection<Button> VoiceCards => voiceCards.Values.Select(c => c.Button).ToList();

    /// <summary>A spinner shows on this AI voice's card (it is loading).</summary>
    internal bool SpinnerShown(string id) => aiSpinners.TryGetValue(id, out var s) && s.IsVisible;

    private void BuildAiSection()
    {
        AiHost.Children.Clear();
        aiSpinners.Clear();
        AddAiVoiceCard = null;
        var ai = controller.Ai;
        AiSection.IsVisible = ai != null;
        if (ai == null) return;
        if (!ai.PackInstalled)
        {
            AiHost.Children.Add(DownloadCard());
            return;
        }
        var panel = new WrapPanel();
        foreach (var v in ItemOps.VisibleVoices(controller.Settings, showHidden))
        {
            if (v.Kind != VoiceKind.Ai) continue;
            var id = v.Id;
            var card = MakeCard(v.Emoji, AppController.DisplayName(v), v.Hotkey, v.Hidden, () => VoiceMenu(v));
            card.Button.Click += (_, _) =>
            {
                if (v.Hidden) controller.SetVoiceHidden(id, false);
                else controller.SelectVoice(id);
            };
            var grid = (Panel)card.Button.Content!;
            grid.Children.Insert(1, new TextBlock
            {
                Text = "🧠",
                FontFamily = Res.Emoji,
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
            });
            var spinner = Spinner();
            grid.Children.Add(spinner);
            aiSpinners[id] = spinner;
            ToolTip.SetTip(card.Button, AiTip(v));
            AttachReorder(card, id, voice: true);
            voiceCards[id] = card;
            aiCardIds.Add(id);
            panel.Children.Add(card.Button);
        }
        AddAiVoiceCard = MakeAddCard(L.T("Add AI voice"), AddAiVoice_Click);
        panel.Children.Add(AddAiVoiceCard);
        AiHost.Children.Add(panel);
    }

    private string AiTip(VoicePreset v)
    {
        var m = controller.Ai?.Find(v.AiVoiceId ?? v.Id);
        string name = AppController.DisplayName(v);
        if (m == null) return name;
        return m.BuiltIn ? name + " — " + L.T("an AI voice that comes with EasyVocal") : name + " — " + L.T("an AI voice you added");
    }

    /// <summary>A turning arc over a card (hidden until its voice loads).</summary>
    private static Control Spinner()
    {
        var arc = new Arc
        {
            Width = 34,
            Height = 34,
            StartAngle = 0,
            SweepAngle = 270,
            Stroke = Res.Brush("Accent"),
            StrokeThickness = 3.5,
            StrokeLineCap = PenLineCap.Round,
            RenderTransform = new RotateTransform(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 22),
        };
        arc.Classes.Add("spin");
        return new Border
        {
            Background = Res.Brush("Scrim"),
            CornerRadius = new CornerRadius(8),
            IsVisible = false,
            IsHitTestVisible = false,
            Child = arc,
        };
    }

    private void UpdateAiCards()
    {
        var loading = controller.AiLoadingVoiceId;
        foreach (var (id, spinner) in aiSpinners) spinner.IsVisible = id == loading;
    }

    private void UpdateAiReadout()
    {
        bool ai = controller.ActiveVoice.Kind == VoiceKind.Ai && controller.FilterEnabled;
        string? text = null;
        if (ai && controller.AiState == AiVoiceState.Loading) text = L.T("AI · loading…");
        else if (ai && controller.AiState == AiVoiceState.Ready) text = L.F("AI · {0} ms", controller.VoiceLatencyMs);
        AiReadout.IsVisible = text != null && Bounds.Width >= 540;
        AiReadoutText.Text = text ?? "";
    }

    private void DismissButton_Click(object? sender, RoutedEventArgs e) => controller.DismissAiProblem();

    // ---------- the AI pack's download ----------

    private Border DownloadCard()
    {
        if (downloadCard != null) return downloadCard;
        downloadButton = new Button { Theme = Res.Theme("AccentButton"), Padding = new Thickness(14, 7), HorizontalAlignment = HorizontalAlignment.Left };
        downloadButton.Click += DownloadAi_Click;
        downloadCancel = new Button { Content = L.T("Cancel"), Margin = new Thickness(8, 0, 0, 0), IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        downloadCancel.Click += (_, _) => downloadCts?.Cancel();
        downloadFill = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(4), Background = Res.Brush("Accent") };
        downloadTrack = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Res.Brush("Bg3"), Margin = new Thickness(0, 10, 0, 0), IsVisible = false, Child = downloadFill };
        downloadTrack.SizeChanged += (_, _) => SetDownloadFill(downloadShare);
        downloadStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), IsVisible = false };

        var head = new DockPanel();
        var plate = EmojiPlate("🧠", 22, 36, 1);
        plate.Margin = new Thickness(0, 0, 10, 0);
        plate.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(plate, Dock.Left);
        head.Children.Add(plate);
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = L.T("Realistic AI voices"), FontWeight = FontWeight.SemiBold, FontSize = 14 });
        texts.Children.Add(new TextBlock
        {
            Text = L.T("Sound like another person on your calls. Works on any PC, and better with a graphics card. A one-time download; remove it any time in Settings."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res.Brush("TextDim"),
            Margin = new Thickness(0, 3, 0, 0),
        });
        head.Children.Add(texts);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(downloadButton);
        buttons.Children.Add(downloadCancel);

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(buttons);
        stack.Children.Add(downloadTrack);
        stack.Children.Add(downloadStatus);
        downloadCard = new Border
        {
            Background = Res.Brush("Bg2"),
            BorderBrush = Res.Brush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
        };
        SetDownloadIdle(null, false);
        return downloadCard;
    }

    private double downloadShare;

    private void SetDownloadFill(double share)
    {
        downloadShare = share;
        if (downloadFill != null && downloadTrack != null) downloadFill.Width = Math.Max(0, downloadTrack.Bounds.Width * Math.Clamp(share, 0, 1));
    }

    private static string Mb(long bytes) => L.F("{0} MB", Math.Max(1, (long)Math.Round(bytes / 1048576.0)));

    private void SetDownloadIdle(string? status, bool problem, long? size = null)
    {
        downloading = false;
        downloadButton!.Content = L.F("Download AI mode (~{0})", Mb(size ?? PackSource.ApproxBytes));
        downloadButton.IsEnabled = true;
        downloadCancel!.IsVisible = false;
        downloadTrack!.IsVisible = false;
        downloadStatus!.IsVisible = status != null;
        downloadStatus.Text = status ?? "";
        downloadStatus.Foreground = problem ? Res.Brush("Danger") : Res.Brush("TextDim");
    }

    private async void DownloadAi_Click(object? sender, RoutedEventArgs e)
    {
        if (controller.Ai is not { } ai || downloading) return;
        downloading = true;
        var cts = downloadCts = new CancellationTokenSource();
        downloadButton!.IsEnabled = false;
        downloadCancel!.IsVisible = true;
        downloadTrack!.IsVisible = true;
        SetDownloadFill(0);
        downloadStatus!.IsVisible = true;
        downloadStatus.Foreground = Res.Brush("TextDim");
        downloadStatus.Text = L.T("Looking for the AI pack…");
        PackOffer? offer = null;
        try
        {
            offer = await ai.FindPackAsync(cts.Token);
            if (controller.IsDisposed) return;
            downloadButton.Content = L.F("Download AI mode (~{0})", Mb(offer.Size));
            long already = ai.Downloaded(offer);
            if (already > 0) downloadStatus.Text = L.F("Going on from {0}…", Mb(already));
            var progress = new UiProgress<PackProgress>(p =>
            {
                if (p.Stage == PackStage.Downloading && p.Total is long total && total > 0)
                {
                    SetDownloadFill(p.Done / (double)total);
                    downloadStatus.Text = L.F("Downloading… {0} of {1}", Mb(p.Done), Mb(total));
                }
                else if (p.Stage == PackStage.Checking)
                {
                    if (p.Total is long all && all > 0) SetDownloadFill(p.Done / (double)all);
                    downloadStatus.Text = L.T("Checking the download…");
                }
                else if (p.Stage is PackStage.Unpacking or PackStage.Installing)
                {
                    SetDownloadFill(1);
                    downloadStatus.Text = L.T("Installing…");
                }
            });
            bool restart = await ai.InstallPackAsync(offer, progress, cts.Token);
            if (controller.IsDisposed) return;
            SetDownloadIdle(null, false, offer.Size); // ready for another time (after Remove AI mode)
            downloadCts = null;
            controller.AiVoicesChanged();
            await TestPcAsync(ai);
            if (restart && !controller.IsDisposed)
            {
                // the runtime the AI voices used is still loaded: the new one takes its place at the next start
                AiStatus.IsVisible = true;
                AiStatus.Text = (AiStatus.Text + " " + L.T("Restart EasyVocal to finish installing AI mode.")).Trim();
            }
        }
        catch (OperationCanceledException)
        {
            if (controller.IsDisposed) return;
            SetDownloadIdle(offer != null && ai.Downloaded(offer) > 0 ? L.T("Paused. Click Download again to go on from where it stopped.") : L.T("Cancelled."), false, offer?.Size);
        }
        catch (Exception ex)
        {
            AppLog.Error("Downloading the AI pack failed", ex);
            if (controller.IsDisposed) return;
            SetDownloadIdle(AiText.Friendly(ex.Message), true, offer?.Size);
        }
        finally
        {
            if (ReferenceEquals(downloadCts, cts)) downloadCts = null;
        }
    }

    /// <summary>The first time after the install: each level is measured and one suggested (saved for the settings).</summary>
    internal async Task TestPcAsync(IAiVoices ai)
    {
        testing = true;
        AiStatus.IsVisible = true;
        AiStatus.Text = L.T("Testing your PC…");
        try
        {
            var result = await ai.BenchmarkAsync(null, CancellationToken.None);
            if (controller.IsDisposed) return;
            controller.SaveBenchmark(result);
            AiStatus.Text = result.AnyRealTime
                ? L.F("Suggested for this PC: {0}. You can change it in Settings.", AiText.QualityName(result.Suggested))
                : L.T("Your PC is slow for AI voices: Light is used and the voice may stutter.");
        }
        catch (Exception ex)
        {
            AppLog.Error("The AI benchmark failed", ex);
            if (!controller.IsDisposed) AiStatus.Text = L.T("Your PC couldn't be tested; Balanced is used.");
        }
        finally
        {
            testing = false;
        }
    }

    // ---------- AI cards ----------

    private ContextMenu AiVoiceMenu(VoicePreset v)
    {
        var id = v.Id;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(L.T("Edit…"), () => FireAndForget.Watch(new AiVoiceWindow(controller, v).ShowAsync(this), "Editing an AI voice")));
        menu.Items.Add(MenuItem(L.T("Set hotkey…"), () => SetVoiceHotkey(id)));
        AddMoveItems(menu, id, voice: true);
        menu.Items.Add(new Separator());
        if (v.Hidden) menu.Items.Add(MenuItem(L.T("Unhide"), () => controller.SetVoiceHidden(id, false)));
        else if (v.BuiltIn) menu.Items.Add(MenuItem(L.T("Hide"), () => controller.SetVoiceHidden(id, true)));
        if (!v.BuiltIn) menu.Items.Add(MenuItem(L.T("Delete…"), () => DeleteVoice(id)));
        return menu;
    }

    private void AddAiVoice_Click(object? sender, RoutedEventArgs e) => FireAndForget.Watch(new AddAiVoiceWindow(controller).ShowAsync(this), "Adding an AI voice");
}

/// <summary>Reports on the UI thread, in order (Avalonia's dispatcher), dropping nothing.</summary>
internal sealed class UiProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => Dispatcher.UIThread.Post(() => report(value));
}

/// <summary>AI texts shown to the person: the engine's messages translated, quality names.</summary>
internal static class AiText
{
    /// <summary>
    /// An engine message in the person's language: the exact sentence when it is translated, else its first part
    /// (before " (" or ":") when that is, else as it is.
    /// </summary>
    public static string Friendly(string message)
    {
        var t = L.T(message);
        if (!ReferenceEquals(t, message) && t != message) return t;
        foreach (var cut in new[] { " (", ": " })
        {
            int i = message.IndexOf(cut, StringComparison.Ordinal);
            if (i <= 0) continue;
            var head = message[..i].TrimEnd('.') + ".";
            var th = L.T(head);
            if (th != head) return th;
        }
        return message;
    }

    public static string QualityName(EasyVocal.Ai.AiQuality q) => q switch
    {
        EasyVocal.Ai.AiQuality.Light => L.T("Light"),
        EasyVocal.Ai.AiQuality.Max => L.T("Max"),
        _ => L.T("Balanced"),
    };
}
