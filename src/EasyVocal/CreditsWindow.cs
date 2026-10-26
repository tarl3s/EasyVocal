using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>Settings → "Credits and licences": THIRD-PARTY-NOTICES.md (who made what EasyVocal uses, and its licence).</summary>
public sealed class CreditsWindow : AppWindow
{
    internal TextBlock Body { get; }

    public CreditsWindow(AppController controller)
    {
        Title = L.T("Credits and licences");
        Width = 620;
        Height = 560;
        MinWidth = 420;
        MinHeight = 320;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Place("credits", controller);

        Body = new TextBlock { Text = Read(), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(20, 16, 20, 8) };
        var close = new Button { Content = L.T("Close"), MinWidth = 96, Theme = Res.Theme("AccentButton"), IsDefault = true, IsCancel = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(20, 8, 20, 16) };
        close.Click += CancelClick;
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = Body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(close, 1);
        root.Children.Add(close);
        Content = root;
    }

    /// <summary>The notices as plain lines (Markdown's marks taken out).</summary>
    internal static string Read()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("EasyVocal.THIRD-PARTY-NOTICES.md");
            if (s == null) return "";
            using var r = new StreamReader(s);
            // Paragraphs and list items each on one line (the file wraps them at 120 columns).
            var sb = new System.Text.StringBuilder();
            foreach (var raw in r.ReadToEnd().Replace("\r", "").Split('\n'))
            {
                var l = raw.Replace("**", "").Replace("`", "");
                if (l.Length == 0) sb.Append("\n\n");
                else if (l.StartsWith("## ", StringComparison.Ordinal)) sb.Append('\n').Append(l[3..].ToUpperInvariant()).Append('\n');
                else if (l.StartsWith("# ", StringComparison.Ordinal)) sb.Append(l[2..]).Append('\n');
                else if (l.StartsWith("- ", StringComparison.Ordinal)) sb.Append('\n').Append("•  ").Append(l[2..]);
                else sb.Append(sb.Length > 0 && sb[^1] != '\n' ? " " : "").Append(l.Trim());
            }
            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "\n{3,}", "\n\n").Trim();
        }
        catch (Exception ex)
        {
            AppLog.Error("Reading the credits failed", ex);
            return "";
        }
    }
}
