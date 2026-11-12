using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Progress;

namespace ADHDWindows.App.Progress;

/// <summary>Пиксельный отчёт по прошедшему дню: монетки, активность, рекорды и лучшая активность.</summary>
internal sealed class DaySummaryWindow : PixelWindow
{
    private const int ContentWidth = 220;

    private readonly StackPanel _page = new();

    public DaySummaryWindow(TextCatalog texts, DaySummary summary)
    {
        Summary = summary;
        Title = texts.Get("progress.summary.title", summary.Date.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture));
        IconSprite = "chart";
        ShowActivated = false;

        var layout = new StackPanel { Width = ContentWidth };
        ProgressViews.Summary(_page, texts, summary);
        layout.Children.Add(_page);
        Ok = new PixelButton { Content = texts.Get("progress.summary.ok"), Kind = PixelButtonKind.Primary, Margin = new Thickness(0, 5, 0, 0), Focusable = false };
        Ok.Click += (_, _) => Close();
        layout.Children.Add(Ok);

        TextElement.SetForeground(layout, PixelAssets.Current.Brush("text"));
        Content = layout;
    }

    public DaySummary Summary { get; }

    public PixelButton Ok { get; }

    internal IReadOnlyList<string> Texts => ProgressViews.Texts(_page);
}
