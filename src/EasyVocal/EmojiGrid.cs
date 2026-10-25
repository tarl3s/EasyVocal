using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace EasyVocal;

/// <summary>A grid of emoji to pick from (nobody types an emoji). The picked one has an accent outline.</summary>
public sealed class EmojiGrid : WrapPanel
{
    /// <summary>The choices: faces and characters first, then animals, sounds and things.</summary>
    public static readonly string[] Choices =
    {
        "🙂", "😀", "😎", "🤩", "😂", "😱", "😡", "😴", "🤪", "🥳",
        "🤖", "👹", "👽", "😈", "👻", "💀", "🤡", "🧙", "🧛", "🧟",
        "👩", "👨", "👶", "👴", "🦸", "🐿️", "🐭", "🐱", "🐶", "🐸",
        "🐵", "🦆", "🐔", "🐷", "🦁", "🐉", "🐻", "🦇", "🐺", "🦄",
        "🎤", "🎧", "🎵", "🎶", "📻", "📢", "📯", "🔔", "🥁", "🎺",
        "🎸", "🎹", "💥", "🔥", "⚡", "❄️", "💧", "🌊", "🚀", "⭐",
        "❤️", "💩", "👏", "🎉",
    };

    private readonly Dictionary<string, Button> cells = new();
    private string selected;

    public EmojiGrid(string? current)
    {
        selected = string.IsNullOrEmpty(current) ? Choices[0] : current;
        var theme = Res.Theme("CardButton");
        var font = Res.Emoji;
        var list = new List<string>(Choices);
        if (!list.Contains(selected)) list.Insert(0, selected); // an emoji from an older version is still there to keep
        foreach (var emoji in list)
        {
            var text = emoji;
            var cell = new Button
            {
                Theme = theme,
                Width = 38,
                Height = 38,
                Margin = new Thickness(0, 0, 4, 4),
                Content = new TextBlock { Text = text, FontFamily = font, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            cell.Click += (_, _) => Selected = text;
            cells[text] = cell;
            Children.Add(cell);
        }
        Highlight();
    }

    /// <summary>Raised when the person picks another emoji.</summary>
    public event Action? SelectionChanged;

    public string Selected
    {
        get => selected;
        set
        {
            if (selected == value || !cells.ContainsKey(value)) return;
            selected = value;
            Highlight();
            SelectionChanged?.Invoke();
        }
    }

    private void Highlight()
    {
        var accent = Res.Brush("Accent");
        foreach (var (emoji, cell) in cells)
            cell.BorderBrush = emoji == selected ? accent : Brushes.Transparent;
    }
}
