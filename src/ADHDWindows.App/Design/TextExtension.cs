using System.Windows.Markup;

namespace ADHDWindows.App.Design;

/// <summary>Текст из каталога для разметки: <c>{d:T showcase.title}</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        PixelAssets.Texts?.Get(key) ?? key;
}
