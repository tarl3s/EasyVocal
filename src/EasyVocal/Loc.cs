using System.Windows.Markup;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>XAML text through the translation table: <c>Text="{loc:T 'Hear me'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
