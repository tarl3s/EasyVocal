using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace EasyVocal;

/// <summary>The theme's resources (Theme.axaml) for code that builds controls.</summary>
internal static class Res
{
    private static object Find(string key) =>
        Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var value) && value != null
            ? value
            : throw new KeyNotFoundException("No theme resource '" + key + "'.");

    public static IBrush Brush(string key) => (IBrush)Find(key);

    public static ControlTheme Theme(string key) => (ControlTheme)Find(key);

    public static FontFamily Font(string key) => (FontFamily)Find(key);

    public static FontFamily Emoji => Font("EmojiFont");
}
