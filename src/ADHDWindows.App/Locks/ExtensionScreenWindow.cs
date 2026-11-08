using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Locks;
using ADHDWindows.Platform;

namespace ADHDWindows.App.Locks;

/// <summary>Просьба включить расширение: без него окна браузера сворачиваются. Фокус не забирает, как экран покупки.</summary>
internal sealed class ExtensionScreenWindow : PixelWindow
{
    private const int ContentWidth = 260;

    private readonly TextCatalog _texts;
    private readonly Action<string> _onOpenPage;
    private readonly StackPanel _rows = new();

    public ExtensionScreenWindow(TextCatalog texts, Action<string> onOpenPage, Action onOpenFolder)
    {
        _texts = texts;
        _onOpenPage = onOpenPage;
        Title = texts.Get("extension.title");
        IconSprite = "lock";
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SourceInitialized += (_, _) => WindowTraits.MakeNonActivating(new WindowInteropHelper(this).Handle);

        var layout = new StackPanel { Width = ContentWidth };
        layout.Children.Add(new PixelText { Text = texts.Get("extension.text"), Wrap = true, Margin = new Thickness(0, 0, 0, 6) });
        layout.Children.Add(_rows);
        layout.Children.Add(new PixelText
        {
            Text = texts.Get("extension.howTo"),
            Wrap = true,
            Foreground = PixelAssets.Current.Brush("mist"),
            Margin = new Thickness(0, 4, 0, 6),
        });

        var buttons = new DockPanel { LastChildFill = false };
        var later = new PixelButton { Content = texts.Get("lock.later"), Focusable = false };
        later.Click += (_, _) => Close();
        DockPanel.SetDock(later, Dock.Right);
        var folder = new PixelButton { Content = texts.Get("extension.folder"), Focusable = false };
        folder.Click += (_, _) => onOpenFolder();
        DockPanel.SetDock(folder, Dock.Left);
        buttons.Children.Add(later);
        buttons.Children.Add(folder);
        layout.Children.Add(buttons);

        TextElement.SetForeground(layout, PixelAssets.Current.Brush("text"));
        Content = layout;
    }

    public void Update(IReadOnlyList<ExtensionProblem> problems)
    {
        _rows.Children.Clear();
        foreach (var problem in problems)
        {
            var open = new PixelButton
            {
                Content = _texts.Get("extension.open"),
                Kind = PixelButtonKind.Primary,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            };
            AutomationProperties.SetAutomationId(open, $"extensions:{problem.BrowserId}");
            open.Click += (_, _) => _onOpenPage(problem.BrowserId);
            DockPanel.SetDock(open, Dock.Right);

            var key = problem.Kind == ExtensionProblemKind.Incognito ? "extension.incognito" : "extension.missing";
            var row = new DockPanel();
            row.Children.Add(open);
            row.Children.Add(new PixelText
            {
                Text = _texts.Get(key, problem.BrowserName),
                Wrap = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            _rows.Children.Add(new PixelFrame { Sprite = "inset", Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 2), Child = row });
        }
    }
}
