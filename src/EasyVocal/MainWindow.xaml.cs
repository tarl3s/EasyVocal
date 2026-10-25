using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Model;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The top bar and the Voices / Sounds tabs. Only shows what <see cref="AppController"/> holds and forwards clicks
/// to it; no audio logic lives here.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppController controller;
    private readonly DispatcherTimer meterTimer;
    private readonly Dictionary<string, Card> voiceCards = new();
    private readonly Dictionary<string, Card> soundCards = new();
    private readonly FontFamily emojiFont;
    private string layoutKey = "";
    private bool showHidden;
    private bool allowClose;
    private double meterShown;

    /// <summary>A grid button and the parts that change while it is shown.</summary>
    private sealed class Card
    {
        public Card(Button button, Border outline)
        {
            Button = button;
            Outline = outline;
        }

        public Button Button { get; }
        public Border Outline { get; }
        public bool Pulsing { get; set; }
    }

    public MainWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();
        DarkTitleBar.Use(this);
        WindowPlacer.Use(this, "main", controller);
        emojiFont = (FontFamily)FindResource("EmojiFont");

        meterTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        meterTimer.Tick += (_, _) => UpdateMeter();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { meterTimer.Start(); UpdateAll(); }
            else meterTimer.Stop();
        };

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

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (allowClose) return;
        e.Cancel = true;
        Hide();
        HiddenToTray?.Invoke();
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
        sb.Append(showHidden).Append('|').Append(L.Language).Append('|');
        foreach (var v in controller.Settings.Voices)
            sb.Append(v.Id).Append(',').Append(v.Name).Append(',').Append(v.Emoji).Append(',').Append(v.Hidden).Append(',').Append(v.Hotkey).Append(';');
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

        var problems = controller.HotkeyProblems;
        HotkeyNote.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HotkeyNote.Text = problems.Count > 0 ? "⚠ " + string.Join(" ", problems) : "";
    }

    /// <summary>The banner's Fix puts Windows' sound back on a real device (instead of opening the setup).</summary>
    private bool fixDefaults;

    private void UpdateBanner()
    {
        fixDefaults = false;
        if (controller.SoundGoesToCable)
        {
            // Hearing nothing at all is the worse problem: this comes first. Nothing changes until Fix is pressed.
            ShowBanner(L.T("Your PC's sound is going into the virtual cable, so you can't hear it."), fix: true, chooseMic: false);
            fixDefaults = true;
            return;
        }
        switch (controller.State)
        {
            case EngineState.MicLost:
                ShowBanner(L.T("Microphone disconnected — reconnecting…"), fix: false, chooseMic: true);
                break;
            case EngineState.NoVirtualCable when controller.MonitorOnly:
                ShowBanner(L.T("Virtual microphone not found. You can still try the voices with 🎧 Hear me."), fix: true, chooseMic: false);
                break;
            case EngineState.OutputLost:
            case EngineState.NoVirtualCable:
                ShowBanner(L.T("Virtual microphone not found"), fix: true, chooseMic: false);
                break;
            case EngineState.Running when controller.MicStandIn is { } standIn:
                // The chosen mic has been gone a while: Windows' default is used for now (the choice is kept).
                ShowBanner(L.F("Your microphone isn't connected, so EasyVocal is using {0} for now.", standIn), fix: false, chooseMic: true);
                break;
            default:
                Banner.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void ShowBanner(string text, bool fix, bool chooseMic)
    {
        BannerText.Text = text;
        FixButton.Visibility = fix ? Visibility.Visible : Visibility.Collapsed;
        MicButton.Visibility = chooseMic ? Visibility.Visible : Visibility.Collapsed;
        Banner.Visibility = Visibility.Visible;
    }

    private void UpdateMeter()
    {
        if (WindowState == WindowState.Minimized) return;
        meterShown = Audio.MicMeter.NextFill(meterShown, controller.InputLevel);
        MeterBar.Width = Math.Max(0, MeterTrack.ActualWidth * meterShown);
    }

    // ---------- cards ----------

    private void BuildVoices()
    {
        VoicePanel.Children.Clear();
        voiceCards.Clear();
        foreach (var v in controller.Settings.Voices)
        {
            if (v.Hidden && !showHidden) continue;
            var id = v.Id;
            var card = MakeCard(v.Emoji, AppController.DisplayName(v), v.Hotkey, v.Hidden);
            card.Button.Click += (_, _) =>
            {
                if (v.Hidden) controller.SetVoiceHidden(id, false);
                else controller.SelectVoice(id);
            };
            card.Button.ContextMenu = VoiceMenu(v);
            voiceCards[id] = card;
            VoicePanel.Children.Add(card.Button);
        }
        VoicePanel.Children.Add(MakeAddCard(L.T("New filter"), NewFilter_Click));
        VoicePanel.Children.Add(MakeComingLaterCard());
    }

    private void BuildSounds()
    {
        SoundPanel.Children.Clear();
        soundCards.Clear();
        foreach (var s in controller.Settings.Sounds)
        {
            if (s.Hidden && !showHidden) continue;
            var id = s.Id;
            bool broken = !s.Hidden && controller.IsSoundBroken(id);
            var card = MakeCard(s.Emoji, AppController.DisplayName(s), s.Hotkey, s.Hidden, broken);
            card.Button.Click += (_, _) =>
            {
                if (s.Hidden) controller.SetSoundHidden(id, false);
                else if (controller.IsSoundBroken(id)) AskDialog.Tell(this, L.T("This sound file couldn't be opened"));
                else controller.ToggleSound(id);
            };
            card.Button.ContextMenu = SoundMenu(s);
            soundCards[id] = card;
            SoundPanel.Children.Add(card.Button);
        }
        SoundPanel.Children.Add(MakeAddCard(L.T("Add sound"), AddSound_Click));
    }

    /// <summary>Active voice outlined; playing sounds outlined and pulsing.</summary>
    private void UpdateCards()
    {
        var accent = (Brush)FindResource("Accent");
        var active = controller.ActiveVoice.Id;
        foreach (var (id, card) in voiceCards)
            card.Button.BorderBrush = id == active ? accent : Brushes.Transparent;
        foreach (var (id, card) in soundCards)
        {
            bool playing = controller.IsPlaying(id);
            card.Button.BorderBrush = playing ? accent : Brushes.Transparent;
            if (playing == card.Pulsing) continue;
            card.Pulsing = playing;
            if (playing)
            {
                var pulse = new DoubleAnimation(0.9, 0.15, TimeSpan.FromMilliseconds(550))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                card.Outline.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                card.Outline.BeginAnimation(OpacityProperty, null);
                card.Outline.Opacity = 0;
            }
        }
    }

    private Card MakeCard(string emoji, string name, string? hotkey, bool hidden, bool broken = false)
    {
        var button = new Button { Style = (Style)FindResource("CardButton"), AllowDrop = true, ToolTip = broken ? L.T("This sound file couldn't be opened") : name };
        var grid = new Grid();

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (broken) stack.Opacity = 0.55; // dimmed, with a ⚠ below: the file didn't load
        stack.Children.Add(new TextBlock
        {
            Text = emoji,
            FontFamily = emojiFont,
            FontSize = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = hidden ? 0.4 : 1,
        });
        stack.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12.5,
            Margin = new Thickness(0, 4, 0, 0),
            MaxWidth = 98,
            MaxHeight = 34, // two lines, then "…"
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = hidden ? (Brush)FindResource("TextDim") : (Brush)FindResource("Text"),
        });
        if (broken)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "⚠",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("Warn"),
            });
        }
        if (hidden)
        {
            stack.Children.Add(new TextBlock
            {
                Text = L.T("Unhide"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("Accent"),
            });
        }
        grid.Children.Add(stack);

        if (!string.IsNullOrEmpty(hotkey) && !hidden)
        {
            grid.Children.Add(new Border
            {
                Background = (Brush)FindResource("Bg0"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 0, 4, 1),
                Margin = new Thickness(0, 4, 5, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = hotkey, FontSize = 10, Foreground = (Brush)FindResource("TextDim") },
            });
        }

        // "⋯" on hover opens the same menu as a right-click.
        var more = new Button
        {
            Content = "⋯",
            Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(5, 0, 5, 2),
            FontSize = 14,
            Margin = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Hidden,
            ToolTip = L.T("More"),
        };
        more.Click += (_, e) =>
        {
            e.Handled = true; // don't let the click reach the card (it would pick the voice / play the sound)
            if (button.ContextMenu == null) return;
            button.ContextMenu.PlacementTarget = more;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        };
        grid.Children.Add(more);
        button.MouseEnter += (_, _) => more.Visibility = Visibility.Visible;
        button.MouseLeave += (_, _) => more.Visibility = Visibility.Hidden;
        button.ContextMenuOpening += (_, _) =>
        {
            if (button.ContextMenu == null) return;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.MousePoint;
        };

        // Pulsing outline for a playing sound (drawn over the card's own border).
        var outline = new Border
        {
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(-2),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        grid.Children.Add(outline);

        button.Content = grid;
        return new Card(button, outline);
    }

    private Button MakeAddCard(string text, RoutedEventHandler click)
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
        var button = new Button { Style = (Style)FindResource("AddCardButton"), Content = stack, ToolTip = text, AllowDrop = true };
        button.Click += click;
        return button;
    }

    /// <summary>Keeps the AI pack's place: a disabled card.</summary>
    private Border MakeComingLaterCard()
    {
        var dim = (Brush)FindResource("TextDim");
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = "🧠", FontFamily = emojiFont, FontSize = 26, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = L.T("AI voices"), FontSize = 12.5, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        stack.Children.Add(new TextBlock { Text = L.T("coming later"), FontSize = 11, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Center });
        return new Border
        {
            Width = 110,
            Height = 96,
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("Bg1"),
            Child = stack,
            ToolTip = L.T("Realistic AI voices will be an optional download in a later version."),
        };
    }

    private ContextMenu VoiceMenu(VoicePreset v)
    {
        var id = v.Id;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(L.T("Edit…"), () => EditVoice(id)));
        menu.Items.Add(MenuItem(L.T("Set hotkey…"), () => SetVoiceHotkey(id)));
        menu.Items.Add(MenuItem(L.T("Duplicate"), () => controller.DuplicateVoice(id)));
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
        if (VoiceById(id) is { } v) new FilterEditorWindow(controller, v) { Owner = this }.ShowDialog();
    }

    private void EditSound(string id)
    {
        if (SoundById(id) is { } s) new SoundEditorWindow(controller, s, null) { Owner = this }.ShowDialog();
    }

    private void EditSoundAudio(string id)
    {
        if (SoundById(id) is { } s) _ = AudioEditorWindow.EditSavedSoundAsync(this, controller, s);
    }

    private void SetVoiceHotkey(string id)
    {
        if (VoiceById(id) is { } v) AssignHotkey("voice:" + id, AppController.DisplayName(v), v.Hotkey, key => v.Hotkey = key);
    }

    private void SetSoundHotkey(string id)
    {
        if (SoundById(id) is { } s) AssignHotkey("sound:" + id, AppController.DisplayName(s), s.Hotkey, key => s.Hotkey = key);
    }

    /// <summary>
    /// Asks for a key, saves it and registers it. If Windows refuses it (another app owns it) the old hotkey
    /// comes back and the person is told.
    /// </summary>
    private void AssignHotkey(string actionId, string name, string? current, Action<string?> set)
    {
        if (!HotkeyCaptureDialog.Pick(this, controller, actionId, name, current, out var chosen)) return;
        set(chosen);
        controller.Save();
        controller.RebuildHotkeys(); // unregisters first; refused keys are listed in HotkeyProblems (shown above the tabs)
        if (chosen == null || !controller.RefusedActions.Contains(actionId)) return;
        set(current);
        controller.Save();
        controller.RebuildHotkeys();
        AskDialog.Tell(this, L.F("Another app is already using {0}. Pick a different one.", chosen));
    }

    private void DeleteVoice(string id)
    {
        if (VoiceById(id) is { } v && ConfirmDelete(AppController.DisplayName(v))) controller.DeleteVoice(id);
    }

    private void DeleteSound(string id)
    {
        if (SoundById(id) is { } s && ConfirmDelete(AppController.DisplayName(s))) controller.DeleteSound(id);
    }

    private bool ConfirmDelete(string name) =>
        AskDialog.Ask(this, L.F("Delete “{0}”? This can't be undone.", name), L.T("Delete"), L.T("Cancel"), null, out _);

    private void NewFilter_Click(object sender, RoutedEventArgs e) => new FilterEditorWindow(controller, null) { Owner = this }.ShowDialog();

    private void AddSound_Click(object sender, RoutedEventArgs e) => OpenSoundEditor(null);

    /// <summary>The sound editor for a new sound, starting from <paramref name="path"/> when given (the e2e test uses it with a file).</summary>
    internal void OpenSoundEditor(string? path) => new SoundEditorWindow(controller, null, path) { Owner = this }.ShowDialog();

    // ---------- drag and drop: sound files dropped on the window add sounds ----------

    protected override void OnPreviewDragOver(DragEventArgs e)
    {
        base.OnPreviewDragOver(e);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    protected override void OnPreviewDrop(DragEventArgs e)
    {
        base.OnPreviewDrop(e);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        e.Handled = true;
        Tabs.SelectedIndex = 1;
        // One editor per file, one after the other (the Cancel button of one stops the rest).
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var file in files)
            {
                var editor = new SoundEditorWindow(controller, null, file) { Owner = this };
                if (editor.ShowDialog() != true) break;
            }
        });
    }

    // ---------- top bar and footers ----------

    private void OnOffButton_Click(object sender, RoutedEventArgs e) => controller.FilterEnabled = OnOffButton.IsChecked == true;

    private void MuteButton_Click(object sender, RoutedEventArgs e) => controller.MicMuted = MuteButton.IsChecked == true;

    private void HearMeButton_Click(object sender, RoutedEventArgs e) => controller.HearMe = HearMeButton.IsChecked == true;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsWindow.Open(controller, this);

    private void FixButton_Click(object sender, RoutedEventArgs e)
    {
        if (!fixDefaults)
        {
            SetupWindow.Open(controller, this);
            return;
        }
        if (!controller.FixDefaults())
            AskDialog.Tell(this, L.T("Windows didn't let EasyVocal change it. Pick your headphones or speakers in the Windows sound settings."));
    }

    private void MicButton_Click(object sender, RoutedEventArgs e) => SettingsWindow.Open(controller, this);

    private void StopAllButton_Click(object sender, RoutedEventArgs e) => controller.StopAllSounds();

    private void SoundsUseVoice_Click(object sender, RoutedEventArgs e) => controller.SoundsUseVoice = SoundsUseVoiceBox.IsChecked == true;

    private void ShowHidden_Click(object sender, RoutedEventArgs e) => SetShowHidden(((CheckBox)sender).IsChecked == true);

    /// <summary>Lists hidden items too (greyed, a click unhides them).</summary>
    internal void SetShowHidden(bool show)
    {
        showHidden = show;
        UpdateAll();
    }
}
