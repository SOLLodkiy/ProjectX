namespace ADHDWindows.App.Design;

/// <summary>Витрина компонентов. Доступна только в режиме разработчика.</summary>
public sealed class ShowcaseWindow : PixelWindow
{
    public ShowcaseWindow()
    {
        Title = PixelAssets.Texts?.Get("showcase.title") ?? "";
        Content = new ShowcaseContent();
    }
}
