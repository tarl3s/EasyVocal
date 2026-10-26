using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EasyVocal.Core.Model;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// An AI voice card's "Edit…": its name and emoji (a voice that came with EasyVocal keeps its pack's), its own quality
/// (or the one in Settings), and where the voice comes from (its licence and credits).
/// </summary>
public sealed class AiVoiceWindow : AppWindow
{
    private readonly AppController controller;
    private readonly VoicePreset voice;
    private readonly EmojiGrid? emoji;

    internal TextBox? NameBox { get; }
    internal ComboBox QualityBox { get; }
    internal Button SaveButton { get; }
    internal TextBlock AboutText { get; }

    public AiVoiceWindow(AppController controller, VoicePreset voice)
    {
        this.controller = controller;
        this.voice = voice;
        Title = L.T("AI voice");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        MinWidth = 380;
        MinHeight = 300;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Place("aiVoice", controller);

        var m = controller.Ai?.Find(voice.AiVoiceId ?? voice.Id);
        var form = new StackPanel { Margin = new Thickness(20, 16, 20, 8) };
        if (voice.BuiltIn)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(MainWindow.EmojiPlate(voice.Emoji, 22, 34, 1));
            head.Children.Add(new TextBlock { Text = AppController.DisplayName(voice), FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            form.Children.Add(head);
            form.Children.Add(new TextBlock { Text = L.T("An AI voice that comes with EasyVocal."), Classes = { "dim" }, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            form.Children.Add(new TextBlock { Text = L.T("Name"), Classes = { "label" } });
            NameBox = new TextBox { MaxLength = 30, Margin = new Thickness(0, 6, 0, 0), Text = voice.Name };
            form.Children.Add(NameBox);
            form.Children.Add(new TextBlock { Text = L.T("Emoji"), Classes = { "label" }, Margin = new Thickness(0, 14, 0, 0) });
            emoji = new EmojiGrid(voice.Emoji);
            form.Children.Add(new Border
            {
                Background = Res.Brush("Bg1"), BorderBrush = Res.Brush("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 8, 4, 4), Margin = new Thickness(0, 6, 0, 0), Child = emoji,
            });
        }

        form.Children.Add(new TextBlock { Text = L.T("AI quality"), Classes = { "label" }, Margin = new Thickness(0, 14, 0, 0) });
        QualityBox = new ComboBox { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (value, label) in new (string?, string)[]
                 {
                     (null, L.T("As in Settings")), ("light", L.T("Light — quickest, lightest on the PC")),
                     ("balanced", L.T("Balanced")), ("max", L.T("Max — most realistic, heaviest")),
                 })
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            QualityBox.Items.Add(item);
            if (value == voice.AiQuality) QualityBox.SelectedItem = item;
        }
        QualityBox.SelectedItem ??= QualityBox.Items[0];
        form.Children.Add(QualityBox);

        AboutText = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "dim", "small" }, Margin = new Thickness(0, 16, 0, 0), Text = About(m) };
        form.Children.Add(AboutText);
        if (!voice.BuiltIn)
            form.Children.Add(new TextBlock { Text = "⚠  " + L.T("Only use voices you have the right to use."), Foreground = Res.Brush("Warn"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });

        SaveButton = new Button { Content = L.T("Save"), MinWidth = 90, Theme = Res.Theme("AccentButton"), IsDefault = true };
        SaveButton.Click += (_, _) => Save();
        var cancel = new Button { Content = L.T("Cancel"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        cancel.Click += CancelClick;
        var buttons = new DockPanel { Margin = new Thickness(20, 8, 20, 16), LastChildFill = false };
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(SaveButton, Dock.Right);
        buttons.Children.Add(cancel);
        buttons.Children.Add(SaveButton);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = form, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        Content = root;
    }

    private static string About(EasyVocal.Ai.Store.VoiceManifest? m)
    {
        if (m == null) return L.T("This voice's files aren't installed.");
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.Description)) lines.Add(m.Description!);
        if (!string.IsNullOrWhiteSpace(m.Source)) lines.Add(L.F("From: {0}", m.Source!));
        if (!string.IsNullOrWhiteSpace(m.Licence)) lines.Add(L.F("Licence: {0}", m.Licence!));
        if (!string.IsNullOrWhiteSpace(m.Attribution)) lines.Add(m.Attribution!);
        return lines.Count == 0 ? L.T("An AI voice you added.") : string.Join("\n", lines);
    }

    /// <summary>The tests: the quality picked by its value.</summary>
    internal void PickQuality(string? value) =>
        QualityBox.SelectedItem = QualityBox.Items.OfType<ComboBoxItem>().First(i => (string?)i.Tag == value);

    internal void Save()
    {
        if (controller.IsDisposed) return;
        if (NameBox != null && !string.IsNullOrWhiteSpace(NameBox.Text)) voice.Name = NameBox.Text.Trim();
        if (emoji != null) voice.Emoji = emoji.Selected;
        voice.AiQuality = (QualityBox.SelectedItem as ComboBoxItem)?.Tag as string;
        controller.VoiceEdited(voice.Id);
        controller.RebuildHotkeys();
        Accept();
    }
}
