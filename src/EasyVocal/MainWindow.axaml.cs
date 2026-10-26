using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Model;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The top bar and the Voices / Sounds tabs. Only shows what <see cref="AppController"/> holds and forwards clicks
/// to it; no audio logic lives here.
/// </summary>
public partial class MainWindow : AppWindow
{
    private readonly AppController controller;
    private readonly DispatcherTimer meterTimer;
    private readonly Dictionary<string, Card> voiceCards = new();
    private readonly Dictionary<string, Card> soundCards = new();
    private string layoutKey = "";
    private bool showHidden;
    private bool allowClose;
    private double meterShown;

    /// <summary>A grid button and the parts that change while it is shown.</summary>
    private sealed class Card
    {
        public Card(Button button, Border outline, Border dropBefore, Border dropAfter)
        {
            Button = button;
            Outline = outline;
            DropBefore = dropBefore;
            DropAfter = dropAfter;
        }

        public Button Button { get; }
        public Border Outline { get; }
        /// <summary>The bars that show where a dragged card would go: before or after this one.</summary>
        public Border DropBefore { get; }
        public Border DropAfter { get; }
    }

    public MainWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();
        Place("main", controller);

        meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        meterTimer.Tick += (_, _) => UpdateMeter();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible) { meterTimer.Start(); UpdateAll(); }
            else meterTimer.Stop();
        };
        Opened += (_, _) => meterTimer.Start();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        SizeChanged += (_, _) => UpdateAiReadout(); // a narrow window keeps the level meter instead
        controller.Changed += UpdateAll;
        UpdateAll();
    }

    /// <summary>Cards on screen (for the smoke test log).</summary>
    internal int VoiceCount => voiceCards.Count;

    internal int SoundCount => soundCards.Count;

    /// <summary>The card of a voice / sound on screen, or null (for the e2e test, which clicks it like a person).</summary>
    internal Button? VoiceCard(string id) => voiceCards.TryGetValue(id, out var c) ? c.Button : null;

    internal Button? SoundCard(string id) => soundCards.TryGetValue(id, out var c) ? c.Button : null;

    /// <summary>The window was closed and is now only in the tray.</summary>
    public event Action? HiddenToTray;

    /// <summary>Lets the next Close really close (quitting); otherwise closing hides to the tray.</summary>
    public void AllowClose() => allowClose = true;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (allowClose || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
        e.Cancel = true;
        WindowPlacer.Remember(this, "main", controller);
        if (!CanHideToTray)
        {
            // No tray icon to bring it back from: keep it in the taskbar.
            WindowState = WindowState.Minimized;
            return;
        }
        Hide();
        HiddenToTray?.Invoke();
    }

    /// <summary>
    /// Closing hides the window in the tray. False when there is no tray icon to come back from (it couldn't be made, or
    /// the desktop has no tray): closing then minimises instead.
    /// </summary>
    public bool CanHideToTray { get; set; } = true;

    /// <summary>Brings the window back (from the tray, minimised, or behind other apps).</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; // the system won't always let a background app take the focus; this at least shows it
        Topmost = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        controller.Changed -= UpdateAll;
        meterTimer.Stop();
        base.OnClosed(e);
    }

    // ---------- refresh ----------

    private void UpdateAll()
    {
        if (!IsVisible && IsLoaded) return; // refreshed when shown again
        var key = LayoutKey();
        if (key != layoutKey)
        {
            layoutKey = key;
            BuildVoices();
            BuildSounds();
        }
        UpdateCards();
        UpdateTopBar();
        UpdateBanner();
    }

    /// <summary>Everything that changes which cards exist or what they say; the grids are rebuilt only when it changes.</summary>
    private string LayoutKey()
    {
        var sb = new StringBuilder();
        sb.Append(showHidden).Append('|').Append(L.Language).Append('|').Append(controller.Ai == null ? "-" : controller.Ai.PackInstalled ? "ai" : "noai").Append('|');
        foreach (var v in controller.Settings.Voices)
            sb.Append(v.Id).Append(',').Append(v.Name).Append(',').Append(v.Emoji).Append(',').Append(v.Hidden).Append(',').Append(v.Hotkey).Append(',').Append(v.Kind).Append(';');
        sb.Append('#');
        foreach (var s in controller.Settings.Sounds)
            sb.Append(s.Id).Append(',').Append(s.Name).Append(',').Append(s.Emoji).Append(',').Append(s.Hidden).Append(',').Append(s.Hotkey)
              .Append(',').Append(controller.IsSoundBroken(s.Id)).Append(';');
        return sb.ToString();
    }

    private void UpdateTopBar()
    {
        bool on = controller.FilterEnabled;
        OnOffButton.IsChecked = on;
        OnOffButton.Content = on ? "● " + L.T("ON") : "○ " + L.T("OFF");
        MuteButton.IsChecked = controller.MicMuted;
        HearMeButton.IsChecked = controller.HearMe;
        ShowHiddenVoices.IsChecked = showHidden;
        ShowHiddenSounds.IsChecked = showHidden;
        SoundsUseVoiceBox.IsChecked = controller.SoundsUseVoice;
        UpdateAiReadout();

        var problems = controller.HotkeyProblems;
        HotkeyNote.IsVisible = problems.Count > 0;
        HotkeyNote.Text = problems.Count > 0 ? "⚠ " + string.Join(" ", problems) : "";
    }

    /// <summary>The banner's Fix puts the system's sound back on a real device (instead of opening the setup).</summary>
    private bool fixDefaults;

    private void UpdateBanner()
    {
        fixDefaults = false;
        if (controller.SoundGoesToCable)
        {
            // Hearing nothing at all is the worse problem: this comes first. Nothing changes until Fix is pressed.
            ShowBanner(L.T(controller.Platform.Texts.SoundGoesToCable), fix: true, chooseMic: false);
            fixDefaults = true;
            return;
        }
        switch (controller.State)
        {
            case EngineState.MicLost when controller.MicPermissionDenied:
                // The system refuses the microphone (privacy settings): only the person can allow it there.
                ShowBanner(L.T(controller.Platform.Texts.MicBlocked), fix: false, chooseMic: false,
                    privacy: controller.Platform.OpenMicSettings != null);
                break;
            case EngineState.MicLost:
                ShowBanner(L.T("Microphone disconnected — reconnecting…"), fix: false, chooseMic: true);
                break;
            case EngineState.NoVirtualCable when controller.PreparingVirtualMic:
                Banner.IsVisible = false; // EasyVocal is making it right now (Linux): not missing
                break;
            case EngineState.NoVirtualCable when controller.MonitorOnly:
                ShowBanner(L.T("Virtual microphone not found. You can still try the voices with 🎧 Hear me."), fix: true, chooseMic: false);
                break;
            case EngineState.OutputLost:
            case EngineState.NoVirtualCable:
                ShowBanner(L.T("Virtual microphone not found"), fix: true, chooseMic: false);
                break;
            case EngineState.Running when controller.MicStandIn is { } standIn:
                // The chosen mic has been gone a while: the system's default is used for now (the choice is kept).
                ShowBanner(L.F("Your microphone isn't connected, so EasyVocal is using {0} for now.", standIn), fix: false, chooseMic: true);
                break;
            default:
                if (controller.AiProblem is string ai) ShowBanner("🧠 " + AiText.Friendly(ai), fix: false, chooseMic: false, dismiss: true);
                else Banner.IsVisible = false;
                break;
        }
    }

    private void ShowBanner(string text, bool fix, bool chooseMic, bool privacy = false, bool dismiss = false)
    {
        BannerText.Text = text;
        DismissButton.IsVisible = dismiss;
        FixButton.IsVisible = fix;
        MicButton.IsVisible = chooseMic;
        PrivacyButton.IsVisible = privacy;
        Banner.IsVisible = true;
    }

    /// <summary>The microphone banner's "Open settings": the system's microphone privacy page.</summary>
    private void PrivacyButton_Click(object? sender, RoutedEventArgs e)
    {
        try { controller.Platform.OpenMicSettings?.Invoke(); }
        catch (Exception ex) { AppLog.Error("Opening the microphone privacy settings failed", ex); }
    }

    private void UpdateMeter()
    {
        if (WindowState == WindowState.Minimized) return;
        meterShown = MicMeter.NextFill(meterShown, controller.InputLevel);
        MeterBar.Width = Math.Max(0, MeterTrack.Bounds.Width * meterShown);
    }

    // ---------- cards ----------

    private void BuildVoices()
    {
        VoicePanel.Children.Clear();
        voiceCards.Clear();
        aiCardIds.Clear();
        foreach (var v in ItemOps.VisibleVoices(controller.Settings, showHidden))
        {
            if (v.Kind == VoiceKind.Ai) continue; // in the AI section
            var id = v.Id;
            var card = MakeCard(v.Emoji, AppController.DisplayName(v), v.Hotkey, v.Hidden, () => VoiceMenu(v));
            card.Button.Click += (_, _) =>
            {
                if (v.Hidden) controller.SetVoiceHidden(id, false);
                else controller.SelectVoice(id);
            };
            AttachReorder(card, id, voice: true);
            voiceCards[id] = card;
            VoicePanel.Children.Add(card.Button);
        }
        VoicePanel.Children.Add(MakeAddCard(L.T("New filter"), NewFilter_Click));
        BuildAiSection();
    }

    private void BuildSounds()
    {
        SoundPanel.Children.Clear();
        soundCards.Clear();
        foreach (var s in ItemOps.VisibleSounds(controller.Settings, showHidden))
        {
            var id = s.Id;
            bool broken = !s.Hidden && controller.IsSoundBroken(id);
            var card = MakeCard(s.Emoji, AppController.DisplayName(s), s.Hotkey, s.Hidden, () => SoundMenu(s), broken);
            card.Button.Click += (_, _) =>
            {
                if (s.Hidden) controller.SetSoundHidden(id, false);
                else if (controller.IsSoundBroken(id)) FireAndForget.Watch(AskDialog.TellAsync(this, L.T("This sound file couldn't be opened")), "A message");
                else controller.ToggleSound(id);
            };
            AttachReorder(card, id, voice: false);
            soundCards[id] = card;
            SoundPanel.Children.Add(card.Button);
        }
        SoundPanel.Children.Add(MakeAddCard(L.T("Add sound"), AddSound_Click));
    }

    /// <summary>Active voice outlined; playing sounds outlined and pulsing.</summary>
    private void UpdateCards()
    {
        var accent = Res.Brush("Accent");
        var active = controller.ActiveVoice.Id;
        foreach (var (id, card) in voiceCards)
            card.Button.BorderBrush = id == active ? accent : Brushes.Transparent;
        UpdateAiCards();
        foreach (var (id, card) in soundCards)
        {
            bool playing = controller.IsPlaying(id);
            card.Button.BorderBrush = playing ? accent : Brushes.Transparent;
            card.Outline.Classes.Set("playing", playing);
        }
    }

    private Card MakeCard(string emoji, string name, string? hotkey, bool hidden, Func<ContextMenu> menu, bool broken = false)
    {
        var button = new Button { Theme = Res.Theme("CardButton") };
        ToolTip.SetTip(button, broken ? L.T("This sound file couldn't be opened") : name);
        AutomationProperties.SetName(button, name);
        var grid = new Panel();

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (broken) stack.Opacity = 0.55; // dimmed, with a ⚠ below: the file didn't load
        stack.Children.Add(EmojiPlate(emoji, 30, 42, hidden ? 0.4 : 1));
        stack.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12.5,
            Margin = new Thickness(0, 4, 0, 0),
            MaxWidth = 98,
            MaxLines = 2, // two lines, then "…"
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = hidden ? Res.Brush("TextDim") : Res.Brush("Text"),
        });
        if (broken)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "⚠",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Res.Brush("Warn"),
            });
        }
        if (hidden)
        {
            stack.Children.Add(new TextBlock
            {
                Text = L.T("Unhide"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Res.Brush("Accent"),
            });
        }
        grid.Children.Add(stack);

        if (!string.IsNullOrEmpty(hotkey) && !hidden)
        {
            grid.Children.Add(new Border
            {
                Background = Res.Brush("Bg0"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 0, 4, 1),
                Margin = new Thickness(0, 4, 5, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = hotkey, FontSize = 10, Foreground = Res.Brush("TextDim") },
            });
        }

        // "⋯" on hover opens the same menu as a right-click.
        var more = new Button
        {
            Content = "⋯",
            Theme = Res.Theme("FlatButton"),
            Padding = new Thickness(5, 0, 5, 2),
            FontSize = 14,
            Margin = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };
        ToolTip.SetTip(more, L.T("More"));
        AutomationProperties.SetName(more, L.T("More"));
        more.Click += (_, e) =>
        {
            e.Handled = true; // don't let the click reach the card (it would pick the voice / play the sound)
            var m = menu();
            m.Placement = PlacementMode.Bottom;
            m.Open(more);
        };
        grid.Children.Add(more);
        button.PointerEntered += (_, _) => more.IsVisible = true;
        button.PointerExited += (_, _) => more.IsVisible = false;
        button.ContextMenu = menu();

        // Pulsing outline for a playing sound (drawn over the card's own border).
        var outline = new Border
        {
            BorderBrush = Res.Brush("Accent"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(-2),
            IsHitTestVisible = false,
        };
        outline.Classes.Add("pulse");
        grid.Children.Add(outline);

        // Where a dragged card would land (MainWindow.Reorder.cs).
        var before = DropBar(HorizontalAlignment.Left);
        var after = DropBar(HorizontalAlignment.Right);
        grid.Children.Add(before);
        grid.Children.Add(after);

        button.Content = grid;
        return new Card(button, outline, before, after);
    }

    /// <summary>An emoji on a faint round plate, so dark emoji (🎵 🦇) still show on the dark card.</summary>
    internal static Border EmojiPlate(string emoji, double fontSize, double size, double opacity) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = Res.Brush("EmojiPlate"),
        HorizontalAlignment = HorizontalAlignment.Center,
        Opacity = opacity,
        Child = new TextBlock
        {
            Text = emoji,
            FontFamily = Res.Emoji,
            FontWeight = FontWeight.Normal,
            FontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private Button MakeAddCard(string text, EventHandler<RoutedEventArgs> click)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = "＋", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            MaxWidth = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var button = new Button { Theme = Res.Theme("AddCardButton"), Content = stack };
        ToolTip.SetTip(button, text);
        AutomationProperties.SetName(button, text);
        button.Click += click;
        return button;
    }

    private ContextMenu VoiceMenu(VoicePreset v)
    {
        if (v.Kind == VoiceKind.Ai) return AiVoiceMenu(v);
        var id = v.Id;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(L.T("Edit…"), () => EditVoice(id)));
        menu.Items.Add(MenuItem(L.T("Set hotkey…"), () => SetVoiceHotkey(id)));
        menu.Items.Add(MenuItem(L.T("Duplicate"), () => controller.DuplicateVoice(id)));
        AddMoveItems(menu, id, voice: true);
        menu.Items.Add(new Separator());
        if (v.Hidden) menu.Items.Add(MenuItem(L.T("Unhide"), () => controller.SetVoiceHidden(id, false)));
        else if (v.BuiltIn && ItemOps.CanHide(v)) menu.Items.Add(MenuItem(L.T("Hide"), () => controller.SetVoiceHidden(id, true)));
        if (!v.BuiltIn) menu.Items.Add(MenuItem(L.T("Delete…"), () => DeleteVoice(id)));
        return menu;
    }

    private ContextMenu SoundMenu(SoundItem s)
    {
        var id = s.Id;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(L.T("Edit…"), () => EditSound(id)));
        menu.Items.Add(MenuItem("✂  " + L.T("Edit audio…"), () => EditSoundAudio(id)));
        menu.Items.Add(MenuItem(L.T("Set hotkey…"), () => SetSoundHotkey(id)));
        menu.Items.Add(MenuItem(L.T("Duplicate"), () => controller.DuplicateSound(id)));
        AddMoveItems(menu, id, voice: false);
        menu.Items.Add(new Separator());
        if (s.Hidden) menu.Items.Add(MenuItem(L.T("Unhide"), () => controller.SetSoundHidden(id, false)));
        else if (s.BuiltIn) menu.Items.Add(MenuItem(L.T("Hide"), () => controller.SetSoundHidden(id, true)));
        if (!s.BuiltIn) menu.Items.Add(MenuItem(L.T("Delete…"), () => DeleteSound(id)));
        return menu;
    }

    private static MenuItem MenuItem(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    // ---------- item actions ----------

    private VoicePreset? VoiceById(string id) => controller.Settings.Voices.FirstOrDefault(v => v.Id == id);

    private SoundItem? SoundById(string id) => controller.Settings.Sounds.FirstOrDefault(s => s.Id == id);

    /// <summary>A built-in voice opens as a copy ("Save as new filter").</summary>
    private void EditVoice(string id)
    {
        if (VoiceById(id) is { } v) FireAndForget.Watch(new FilterEditorWindow(controller, v).ShowAsync(this), "The filter editor");
    }

    private void EditSound(string id)
    {
        if (SoundById(id) is { } s) FireAndForget.Watch(new SoundEditorWindow(controller, s, null).ShowAsync(this), "The sound editor");
    }

    private void EditSoundAudio(string id)
    {
        if (SoundById(id) is { } s) FireAndForget.Watch(AudioEditorWindow.EditSavedSoundAsync(this, controller, s), "The audio editor");
    }

    private void SetVoiceHotkey(string id)
    {
        if (VoiceById(id) is { } v) FireAndForget.Watch(AssignHotkeyAsync("voice:" + id, AppController.DisplayName(v), v.Hotkey, key => v.Hotkey = key), "Assigning a hotkey");
    }

    private void SetSoundHotkey(string id)
    {
        if (SoundById(id) is { } s) FireAndForget.Watch(AssignHotkeyAsync("sound:" + id, AppController.DisplayName(s), s.Hotkey, key => s.Hotkey = key), "Assigning a hotkey");
    }

    /// <summary>
    /// Asks for a key, saves it and registers it. If the system refuses it (another app owns it) the old hotkey
    /// comes back and the person is told.
    /// </summary>
    private async Task AssignHotkeyAsync(string actionId, string name, string? current, Action<string?> set)
    {
        var pick = await HotkeyCaptureDialog.PickAsync(this, controller, actionId, name, current);
        if (!pick.Ok || controller.IsDisposed) return; // cancelled, or EasyVocal quit meanwhile
        var chosen = pick.Hotkey;
        set(chosen);
        controller.Save();
        controller.RebuildHotkeys(); // unregisters first; refused keys are listed in HotkeyProblems (shown above the tabs)
        if (chosen == null || !controller.RefusedActions.Contains(actionId)) return;
        set(current);
        controller.Save();
        controller.RebuildHotkeys();
        await AskDialog.TellAsync(this, L.F("Another app is already using {0}. Pick a different one.", chosen));
    }

    private async void DeleteVoice(string id)
    {
        if (VoiceById(id) is { } v && await ConfirmDeleteAsync(AppController.DisplayName(v)) && !controller.IsDisposed) controller.DeleteVoice(id);
    }

    private async void DeleteSound(string id)
    {
        if (SoundById(id) is { } s && await ConfirmDeleteAsync(AppController.DisplayName(s)) && !controller.IsDisposed) controller.DeleteSound(id);
    }

    private async Task<bool> ConfirmDeleteAsync(string name) =>
        (await AskDialog.AskAsync(this, L.F("Delete “{0}”? This can't be undone.", name), L.T("Delete"), L.T("Cancel"), null)).Yes;

    private void NewFilter_Click(object? sender, RoutedEventArgs e) => FireAndForget.Watch(new FilterEditorWindow(controller, null).ShowAsync(this), "The filter editor");

    private void AddSound_Click(object? sender, RoutedEventArgs e) => OpenSoundEditor(null);

    /// <summary>The sound editor for a new sound, starting from <paramref name="path"/> when given (the e2e test uses it with a file).</summary>
    internal void OpenSoundEditor(string? path) => FireAndForget.Watch(new SoundEditorWindow(controller, null, path).ShowAsync(this), "The sound editor");

    // ---------- drag and drop: sound files dropped on the window add sounds ----------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = (e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>())
            .Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (files.Count == 0) return;
        e.Handled = true;
        Tabs.SelectedIndex = 1;
        // One editor per file, one after the other (the Cancel button of one stops the rest).
        foreach (var file in files)
        {
            var editor = new SoundEditorWindow(controller, null, file);
            await editor.ShowAsync(this);
            if (!editor.Accepted) break;
        }
    }

    // ---------- top bar and footers ----------

    private void OnOffButton_Click(object? sender, RoutedEventArgs e) => controller.FilterEnabled = OnOffButton.IsChecked == true;

    private void MuteButton_Click(object? sender, RoutedEventArgs e) => controller.MicMuted = MuteButton.IsChecked == true;

    private void HearMeButton_Click(object? sender, RoutedEventArgs e) => controller.HearMe = HearMeButton.IsChecked == true;

    private void SettingsButton_Click(object? sender, RoutedEventArgs e) => FireAndForget.Watch(SettingsWindow.OpenAsync(controller, this), "Settings");

    private async void FixButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!fixDefaults)
        {
            await SetupWindow.OpenAsync(controller, this);
            return;
        }
        if (!controller.FixDefaults())
            await AskDialog.TellAsync(this, L.T(controller.Platform.Texts.FixRefused));
    }

    private void MicButton_Click(object? sender, RoutedEventArgs e) => FireAndForget.Watch(SettingsWindow.OpenAsync(controller, this), "Settings");

    private void StopAllButton_Click(object? sender, RoutedEventArgs e) => controller.StopAllSounds();

    private void SoundsUseVoice_Click(object? sender, RoutedEventArgs e) => controller.SoundsUseVoice = SoundsUseVoiceBox.IsChecked == true;

    private void ShowHidden_Click(object? sender, RoutedEventArgs e) => SetShowHidden(((CheckBox)sender!).IsChecked == true);

    /// <summary>Lists hidden items too (greyed, a click unhides them).</summary>
    internal void SetShowHidden(bool show)
    {
        showHidden = show;
        UpdateAll();
    }
}
