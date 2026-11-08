using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Platform;

namespace ADHDWindows.App.Time;

/// <summary>
/// Вопрос «Ты тут?». Фокус не забирает, чтобы не выбить из игры или полноэкранного видео,
/// и отвечается только щелчком по кнопке: клавиатура до окна не доходит.
/// </summary>
internal sealed class PresenceWindow : PixelWindow
{
    private const int ContentWidth = 200;

    public PresenceWindow(TextCatalog texts, string activity, Action onConfirm)
    {
        Title = texts.Get("presence.title");
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SourceInitialized += (_, _) => WindowTraits.MakeNonActivating(new WindowInteropHelper(this).Handle);

        var layout = new StackPanel { Width = ContentWidth };
        var key = activity == "game" ? "presence.text.game" : "presence.text.video";
        layout.Children.Add(new PixelText { Text = texts.Get(key), Wrap = true, Margin = new Thickness(0, 0, 0, 6) });

        var confirm = new PixelButton
        {
            Content = texts.Get("presence.confirm"),
            Kind = PixelButtonKind.Primary,
            HorizontalAlignment = HorizontalAlignment.Right,
            Focusable = false,
        };
        AutomationProperties.SetAutomationId(confirm, "presence:confirm");
        confirm.Click += (_, _) =>
        {
            onConfirm();
            Close();
        };
        layout.Children.Add(confirm);

        TextElement.SetForeground(layout, PixelAssets.Current.Brush("text"));
        Content = layout;
    }
}
