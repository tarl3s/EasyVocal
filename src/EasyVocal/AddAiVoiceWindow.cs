using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using EasyVocal.Ai.Store;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// "＋ Add AI voice": an RVC voice from files (.pth or .onnx, its .index if it has one, or a .zip with them) or from a
/// link (Hugging Face pages work), with a name and an emoji. The import runs in the background with its progress and a
/// Cancel; what can't be used says why (an RVC v1 voice, one without pitch, a half-precision ONNX…).
/// </summary>
public sealed class AddAiVoiceWindow : AppWindow
{
    private readonly AppController controller;
    private readonly List<string> files = new();
    private readonly TabControl tabs;
    private readonly TextBlock fileList;
    private readonly EmojiGrid emoji;
    private readonly Border track, fill;
    private CancellationTokenSource? running;

    internal TextBox LinkBox { get; }
    internal TextBox NameBox { get; }
    internal Button AddButton { get; }
    internal Button CancelButton { get; }
    internal Button ChooseButton { get; }
    internal TextBlock StatusText { get; }
    internal TextBlock ErrorText { get; }
    internal TextBlock RightsText { get; }

    /// <summary>The id of the voice card it added (null until then).</summary>
    internal string? AddedId { get; private set; }

    public AddAiVoiceWindow(AppController controller)
    {
        this.controller = controller;
        Title = L.T("Add AI voice");
        Width = 500;
        SizeToContent = SizeToContent.Height;
        MinWidth = 400;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Place("addAiVoice", controller);

        // ---- from a file / from a link ----
        ChooseButton = new Button { Content = L.T("Choose files…"), HorizontalAlignment = HorizontalAlignment.Left };
        ChooseButton.Click += Choose_Click;
        fileList = new TextBlock { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Text = L.T("No file chosen yet.") };
        var fromFile = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        fromFile.Children.Add(new TextBlock { Text = L.T("A .pth or .onnx voice, with its .index if it has one, or a .zip with them."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        fromFile.Children.Add(ChooseButton);
        fromFile.Children.Add(fileList);

        LinkBox = new TextBox { Watermark = "https://huggingface.co/…" };
        LinkBox.TextChanged += (_, _) => { if (ErrorText != null) ErrorText.IsVisible = false; if (AddButton != null) Refresh(); };
        var fromLink = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        fromLink.Children.Add(new TextBlock { Text = L.T("A link to a .pth, .onnx or .zip file (a Hugging Face page works too)."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        fromLink.Children.Add(LinkBox);

        tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = Header("📁", L.T("From a file")), Content = fromFile });
        tabs.Items.Add(new TabItem { Header = Header("🔗", L.T("From a link")), Content = fromLink });
        tabs.SelectionChanged += (_, _) => { if (ErrorText != null) ErrorText.IsVisible = false; if (AddButton != null) Refresh(); };

        // ---- name and emoji ----
        NameBox = new TextBox { MaxLength = 30, Margin = new Thickness(0, 6, 0, 0), Watermark = L.T("From the file's name") };
        emoji = new EmojiGrid("🧠");
        var emojiBox = new Border
        {
            Background = Res.Brush("Bg1"), BorderBrush = Res.Brush("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 8, 4, 4), Margin = new Thickness(0, 6, 0, 0), Child = emoji,
        };

        RightsText = new TextBlock
        {
            Text = "⚠  " + L.T("Only use voices you have the right to use."),
            Foreground = Res.Brush("Warn"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 6, 20, 0), // under the form, always on screen
        };
        fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(4), Background = Res.Brush("Accent") };
        track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Res.Brush("Bg2"), Margin = new Thickness(0, 12, 0, 0), IsVisible = false, Child = fill };
        StatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), IsVisible = false, Classes = { "dim" } };
        ErrorText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), IsVisible = false, Foreground = Res.Brush("Danger") };

        var form = new StackPanel { Margin = new Thickness(20, 16, 20, 8) };
        form.Children.Add(tabs);
        form.Children.Add(new TextBlock { Text = L.T("Name"), Classes = { "label" }, Margin = new Thickness(0, 16, 0, 0) });
        form.Children.Add(NameBox);
        form.Children.Add(new TextBlock { Text = L.T("Emoji"), Classes = { "label" }, Margin = new Thickness(0, 14, 0, 0) });
        form.Children.Add(emojiBox);
        form.Children.Add(track);
        form.Children.Add(StatusText);
        form.Children.Add(ErrorText);

        AddButton = new Button { Content = L.T("Add"), MinWidth = 90, Theme = Res.Theme("AccentButton"), IsDefault = true };
        AddButton.Click += Add_Click;
        CancelButton = new Button { Content = L.T("Cancel"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        CancelButton.Click += Cancel_Click;
        var buttons = new DockPanel { Margin = new Thickness(20, 10, 20, 16), LastChildFill = false };
        DockPanel.SetDock(CancelButton, Dock.Right);
        DockPanel.SetDock(AddButton, Dock.Right);
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(AddButton);

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);
        var bottom = new StackPanel();
        bottom.Children.Add(RightsText);
        bottom.Children.Add(buttons);
        Grid.SetRow(bottom, 1);
        root.Children.Add(bottom);
        Content = root;
        track.SizeChanged += (_, _) => SetFill(share);
        Closing += (_, _) => running?.Cancel();
        Refresh();
    }

    private static StackPanel Header(string icon, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new Border { Classes = { "plate" }, Margin = new Thickness(0, 0, 4, 0), Child = new TextBlock { Text = icon, Classes = { "emoji" } } });
        p.Children.Add(new TextBlock { Text = text });
        return p;
    }

    private bool FromLink => tabs.SelectedIndex == 1;

    /// <summary>The tests: files as if picked, or a link typed.</summary>
    internal void UseFiles(IEnumerable<string> paths)
    {
        tabs.SelectedIndex = 0;
        files.Clear();
        files.AddRange(paths);
        ErrorText.IsVisible = false;
        Refresh();
    }

    internal void UseLink(string link)
    {
        tabs.SelectedIndex = 1;
        LinkBox.Text = link;
        Refresh();
    }

    private void Refresh()
    {
        fileList.Text = files.Count == 0 ? L.T("No file chosen yet.") : string.Join("\n", files.Select(System.IO.Path.GetFileName));
        AddButton.IsEnabled = running == null && (FromLink ? !string.IsNullOrWhiteSpace(LinkBox.Text) : files.Count > 0);
        ChooseButton.IsEnabled = LinkBox.IsEnabled = NameBox.IsEnabled = tabs.IsEnabled = running == null;
    }

    private async void Choose_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("Choose files…"),
                AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType(L.T("AI voice files")) { Patterns = new[] { "*.pth", "*.onnx", "*.index", "*.zip", "*.f32" } } },
            });
            var paths = picked.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0) UseFiles(paths);
        }
        catch (Exception ex) { AppLog.Error("The file picker failed", ex); }
    }

    private double share;

    private void SetFill(double s)
    {
        share = s;
        fill.Width = Math.Max(0, track.Bounds.Width * Math.Clamp(s, 0, 1));
    }

    private async void Add_Click(object? sender, RoutedEventArgs e) => await AddAsync();

    /// <summary>Imports what was given; closes with the new card on success, shows why on failure.</summary>
    internal async Task AddAsync()
    {
        if (controller.Ai is not { } ai || running != null) return;
        var cts = running = new CancellationTokenSource();
        ErrorText.IsVisible = false;
        track.IsVisible = true;
        SetFill(0);
        StatusText.IsVisible = true;
        StatusText.Text = L.T("Getting the voice ready…");
        Refresh();
        string? name = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim();
        var progress = new UiProgress<ImportProgress>(p =>
        {
            if (running != cts) return;
            switch (p.Stage)
            {
                case ImportStage.Downloading when p.Total is long t && t > 0:
                    SetFill(p.Done / (double)t);
                    StatusText.Text = L.F("Downloading… {0} of {1}", L.F("{0} MB", Math.Round(p.Done / 1048576.0, 1)), L.F("{0} MB", Math.Round(t / 1048576.0, 1)));
                    break;
                case ImportStage.Downloading:
                    StatusText.Text = L.F("Downloading… {0}", L.F("{0} MB", Math.Round(p.Done / 1048576.0, 1)));
                    break;
                case ImportStage.Unpacking:
                    StatusText.Text = L.T("Unpacking…");
                    break;
                case ImportStage.Checking:
                    SetFill(1);
                    StatusText.Text = L.T("Checking the voice…");
                    break;
                case ImportStage.Copying:
                    StatusText.Text = L.T("Saving…");
                    break;
            }
        });
        try
        {
            var m = FromLink
                ? await ai.ImportUrlAsync(LinkBox.Text!.Trim(), name, emoji.Selected, progress, cts.Token)
                : await ai.ImportFilesAsync(files.ToList(), name, emoji.Selected, progress, cts.Token);
            if (controller.IsDisposed) return;
            m.Emoji = emoji.Selected;
            if (name != null) m.Name = name;
            AddedId = controller.AddAiVoice(m);
            running = null;
            Accept();
        }
        catch (OperationCanceledException)
        {
            running = null;
            if (IsVisible) Fail(L.T("Cancelled."), problem: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Adding an AI voice failed", ex);
            running = null;
            if (IsVisible) Fail(AiText.Friendly(ex.Message), problem: true);
        }
    }

    private void Fail(string text, bool problem)
    {
        track.IsVisible = false;
        StatusText.IsVisible = false;
        ErrorText.Text = text;
        ErrorText.Foreground = problem ? Res.Brush("Danger") : Res.Brush("TextDim");
        ErrorText.IsVisible = true;
        Refresh();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (running != null)
        {
            running.Cancel(); // stops the import; the window stays to say so
            return;
        }
        Close();
    }
}
