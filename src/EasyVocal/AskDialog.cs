using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>A themed yes/no question, optionally with a checkbox (e.g. "Don't ask again"), and a plain message.</summary>
public static class AskDialog
{
    /// <summary>A themed message with one OK button. The text is already translated.</summary>
    public static void Tell(Window? owner, string message) => Ask(owner, message, L.T("OK"), null, null, out _);

    /// <summary>True when the person picked <paramref name="yes"/>. Texts are already translated; no <paramref name="no"/> = a single button.</summary>
    public static bool Ask(Window? owner, string message, string yes, string? no, string? checkText, out bool isChecked)
    {
        var res = Application.Current.Resources;
        var window = new Window
        {
            Title = "EasyVocal",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)res["Bg0"],
            Foreground = (Brush)res["Text"],
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            ShowInTaskbar = owner == null,
            Topmost = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner != null) window.Owner = owner;
        DarkTitleBar.Use(window);
        WindowPlacer.Use(window, "ask", null);
        if (owner == null && Application.Current.MainWindow?.Icon != null) window.Icon = Application.Current.MainWindow.Icon;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });

        CheckBox? check = null;
        if (checkText != null)
        {
            check = new CheckBox { Content = checkText, Margin = new Thickness(0, 14, 0, 0) };
            root.Children.Add(check);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var yesButton = new Button { Content = yes, MinWidth = 90, Style = (Style)res["AccentButton"] };
        yesButton.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(yesButton);
        Button focus = yesButton;
        if (no != null)
        {
            var noButton = new Button { Content = no, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true, IsDefault = true };
            buttons.Children.Add(noButton);
            focus = noButton; // the safe answer is the default
        }
        else
        {
            yesButton.IsDefault = true;
            yesButton.IsCancel = true;
        }
        root.Children.Add(buttons);
        window.Content = root;
        window.Loaded += (_, _) => focus.Focus();

        bool result = window.ShowDialog() == true;
        isChecked = check?.IsChecked == true;
        return result;
    }
}
