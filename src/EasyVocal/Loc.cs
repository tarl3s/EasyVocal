using Avalonia.Markup.Xaml;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>XAML text through the translation table: <c>Text="{loc:T 'Hear me'}"</c> (with <c>xmlns:loc="using:EasyVocal"</c>).</summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string text) => Text = text;

    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
