using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using EasyVocal.Core.Model;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// Makes or edits a voice filter: a name, an emoji, a starting voice and nine sliders (a built-in voice's hidden
/// extras, like Demon's octave layer, come along with it). Every drag is heard live:
/// the engine plays the sliders (<see cref="AppController.PreviewSliders"/>), in the headphones while the window's own
/// 🎧 Hear me toggle is on (it starts on; <see cref="AppController.PreviewHearMe"/>). The person's saved Hear me is never
/// changed, and everything is put back when the window closes.
/// Editing a built-in voice opens a copy (it can't be changed): the button says "Save as new filter".
/// </summary>
public partial class FilterEditorWindow : AppWindow
{
    private sealed record Row(Slider Slider, Func<VoiceSliders, double> Get, Action<VoiceSliders, double> Set, bool Snaps);

    private readonly AppController controller;
    private readonly VoicePreset? editing;      // the custom voice changed in place; null = a new voice will be added
    private readonly List<Row> rows = new();
    private readonly EmojiGrid emojiGrid;
    private VoiceSliders current;
    private VoiceSliders baseline;               // what "Reset" returns to
    private bool loading = true;

    /// <param name="editing">The voice to edit (a built-in opens as a copy), or null for a brand-new filter that starts from Normal.</param>
    public FilterEditorWindow(AppController controller, VoicePreset? editing)
    {
        this.controller = controller;
        InitializeComponent();
        Place("filterEditor", controller);

        bool asCopy = editing == null || editing.BuiltIn;
        this.editing = asCopy ? null : editing;
        current = (editing?.Sliders ?? new VoiceSliders()).Clone();
        baseline = current.Clone();

        var names = controller.Settings.Voices.Select(v => AppController.DisplayName(v));
        string name = editing == null ? L.T("My filter")
            : editing.BuiltIn ? L.F("{0} copy", AppController.DisplayName(editing))
            : editing.Name;
        NameBox.Text = asCopy ? ItemOps.UniqueName(names, name) : name;

        emojiGrid = new EmojiGrid(editing?.Emoji ?? "🎤");
        EmojiHost.Content = emojiGrid;

        FillStartBox(editing);
        BuildSliders();
        ShowValues();
        SaveButton.Content = editing is { BuiltIn: true } ? L.T("Save as new filter") : L.T("Save");
        UpdateSave();

        // Hear what the sliders do: the editor's own Hear me starts on (shown, and it can be turned off).
        HearToggle.IsChecked = true;
        controller.PreviewHearMe(true);
        controller.PreviewSliders(current);

        loading = false;
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        Closed += (_, _) =>
        {
            controller.PreviewSliders(null);
            controller.PreviewHearMe(null);
        };
    }

    /// <summary>The nine sliders, in order (for the tests).</summary>
    internal IReadOnlyList<Slider> Sliders => rows.Select(r => r.Slider).ToList();

    private void HearToggle_Click(object? sender, RoutedEventArgs e) => controller.PreviewHearMe(HearToggle.IsChecked == true);

    // ---------- start from ----------

    private void FillStartBox(VoicePreset? editing)
    {
        foreach (var v in controller.Settings.Voices)
        {
            if (v.Hidden && v.Id != editing?.Id) continue;
            StartBox.Items.Add(new ComboBoxItem { Content = v.Emoji + "  " + AppController.DisplayName(v), Tag = v });
        }
        // A new filter starts from Normal; a copy of a built-in from that voice; editing your own filter from nothing.
        var start = editing == null ? controller.Settings.Voices.FirstOrDefault(v => v.Id == AppController.NormalVoiceId)
            : editing.BuiltIn ? editing : null;
        foreach (var item in StartBox.Items.OfType<ComboBoxItem>())
            if (ReferenceEquals(item.Tag, start)) StartBox.SelectedItem = item;
    }

    private void StartBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading || StartBox.SelectedItem is not ComboBoxItem { Tag: VoicePreset voice }) return;
        current = voice.Sliders.Clone();
        baseline = current.Clone();
        ShowValues();
        controller.PreviewSliders(current);
    }

    // ---------- sliders ----------

    private void BuildSliders()
    {
        AddRow(L.T("Pitch"), L.T("Deeper"), L.T("Higher"), -1, v => v.Pitch, (v, x) => v.Pitch = x, snaps: true);
        AddRow(L.T("Voice body"), L.T("Bigger"), L.T("Smaller"), -1, v => v.Body, (v, x) => v.Body = x, snaps: true);
        AddRow(L.T("Robot"), L.T("None"), L.T("Lots"), 0, v => v.Robot, (v, x) => v.Robot = x);
        AddRow(L.T("Echo"), L.T("None"), L.T("Lots"), 0, v => v.Echo, (v, x) => v.Echo = x);
        AddRow(L.T("Old radio"), L.T("None"), L.T("Lots"), 0, v => v.Radio, (v, x) => v.Radio = x);
        AddRow(L.T("Distortion"), L.T("None"), L.T("Lots"), 0, v => v.Distortion, (v, x) => v.Distortion = x);
        AddRow(L.T("Whisper"), L.T("None"), L.T("Lots"), 0, v => v.Whisper, (v, x) => v.Whisper = x);
        AddRow(L.T("Growl"), L.T("None"), L.T("Lots"), 0, v => v.Growl, (v, x) => v.Growl = x);
        AddRow(L.T("Big room"), L.T("None"), L.T("Huge"), 0, v => v.Room, (v, x) => v.Room = x);
    }

    private void AddRow(string title, string left, string right, double min, Func<VoiceSliders, double> get, Action<VoiceSliders, double> set, bool snaps = false)
    {
        var dim = Res.Brush("TextDim");
        var slider = new Slider { Minimum = min, Maximum = 1, SmallChange = 0.02, LargeChange = 0.1, VerticalAlignment = VerticalAlignment.Center };
        var row = new Row(slider, get, set, snaps);
        slider.ValueChanged += (_, e) => OnSliderMoved(row, e.NewValue);
        rows.Add(row);

        var line = new Grid { Margin = new Thickness(0, 2, 0, 0), ColumnDefinitions = new ColumnDefinitions("64,*,64") };
        var l = new TextBlock { Text = left, Foreground = dim, FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var r = new TextBlock { Text = right, Foreground = dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(r, 2);
        line.Children.Add(l);
        line.Children.Add(slider);
        line.Children.Add(r);

        SliderPanel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 10, 0, 0) });
        SliderPanel.Children.Add(line);
    }

    private void OnSliderMoved(Row row, double value)
    {
        if (loading) return;
        if (row.Snaps && Math.Abs(value) < 0.04 && value != 0)
        {
            row.Slider.Value = 0; // a soft stop in the middle; this raises the event again with 0
            return;
        }
        row.Set(current, value);
        controller.PreviewSliders(current);
    }

    /// <summary>Moves every slider to <see cref="current"/> without playing it step by step.</summary>
    private void ShowValues()
    {
        bool was = loading;
        loading = true;
        foreach (var row in rows) row.Slider.Value = row.Get(current);
        loading = was;
    }

    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        current = baseline.Clone();
        ShowValues();
        controller.PreviewSliders(current);
    }

    // ---------- save ----------

    private void NameBox_TextChanged(object? sender, TextChangedEventArgs e) => UpdateSave();

    private void UpdateSave() => SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) return;
        if (editing != null)
        {
            editing.Name = name;
            editing.Emoji = emojiGrid.Selected;
            editing.Sliders = current.Clamp();
            controller.VoiceEdited(editing.Id);
        }
        else
        {
            controller.AddVoice(new VoicePreset
            {
                Id = ItemOps.NewId(),
                Name = name,
                Emoji = emojiGrid.Selected,
                Sliders = current.Clamp(),
            });
        }
        Accept();
    }
}
