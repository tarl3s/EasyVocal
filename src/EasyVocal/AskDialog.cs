using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>A themed yes/no question, optionally with a checkbox (e.g. "Don't ask again"), and a plain message.</summary>
public sealed class AskDialog : AppWindow
{
    /// <summary>The person's answer: <see cref="Yes"/> = the first button; <see cref="Checked"/> = the checkbox was ticked.</summary>
    public readonly record struct Answer(bool Yes, bool Checked);

    private readonly CheckBox? check;

    private AskDialog(string message, string yes, string? no, string? checkText)
    {
        Title = "EasyVocal";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Place("ask", null);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 380 });
        if (checkText != null)
        {
            check = new CheckBox { Content = checkText, Margin = new Thickness(0, 14, 0, 0) };
            root.Children.Add(check);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var yesButton = new Button { Content = yes, MinWidth = 90, Theme = Res.Theme("AccentButton") };
        yesButton.Click += (_, _) => Accept();
        buttons.Children.Add(yesButton);
        Button focus = yesButton;
        if (no != null)
        {
            var noButton = new Button { Content = no, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true, IsDefault = true };
            noButton.Click += CancelClick;
            buttons.Children.Add(noButton);
            focus = noButton; // the safe answer is the default
        }
        else
        {
            yesButton.IsDefault = true;
            yesButton.IsCancel = true;
        }
        root.Children.Add(buttons);
        Content = root;
        Opened += (_, _) => focus.Focus();
    }

    /// <summary>The buttons, for the tests.</summary>
    internal CheckBox? CheckBox => check;

    /// <summary>A themed message with one OK button. The text is already translated.</summary>
    public static Task TellAsync(Window? owner, string message) => AskAsync(owner, message, L.T("OK"), null, null);

    /// <summary>Asks and waits. Texts are already translated; no <paramref name="no"/> = a single button.</summary>
    public static async Task<Answer> AskAsync(Window? owner, string message, string yes, string? no, string? checkText)
    {
        var dialog = new AskDialog(message, yes, no, checkText);
        await dialog.ShowAsync(owner);
        return new Answer(dialog.Accepted, dialog.check?.IsChecked == true);
    }
}
