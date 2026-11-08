using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;

namespace ADHDWindows.App.Stats;

/// <summary>
/// Подтверждение сброса прогресса. Кнопка сброса включается через несколько секунд:
/// случайный двойной клик или Enter не сотрёт прогресс.
/// </summary>
internal sealed class ResetWindow : PixelWindow
{
    public const int DelaySeconds = 3;

    private readonly TextCatalog _texts;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _left = DelaySeconds;

    public ResetWindow(TextCatalog texts)
    {
        _texts = texts;
        Title = texts.Get("reset.title");
        IconSprite = "warning";
        ShowInTaskbar = false;

        var layout = new StackPanel { Width = 220 };
        layout.Children.Add(new PixelText { Text = texts.Get("reset.text"), Wrap = true, Margin = new Thickness(0, 0, 0, 6) });

        var buttons = new DockPanel { LastChildFill = false };
        Confirm = new PixelButton { Kind = PixelButtonKind.Danger, IsEnabled = false, Focusable = false };
        Confirm.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        AutomationProperties.SetAutomationId(Confirm, "reset.confirm");
        DockPanel.SetDock(Confirm, Dock.Right);

        var cancel = new PixelButton { Content = texts.Get("reset.cancel"), IsCancel = true, IsDefault = true, Margin = new Thickness(0, 0, 3, 0) };
        cancel.Click += (_, _) => Close();
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(Confirm);
        buttons.Children.Add(cancel);
        layout.Children.Add(buttons);

        TextElement.SetForeground(layout, PixelAssets.Current.Brush("text"));
        Content = layout;

        ShowCountdown();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    public PixelButton Confirm { get; }

    /// <summary>Секунда ожидания, для проверок вызывается напрямую.</summary>
    internal void Tick()
    {
        _left = Math.Max(0, _left - 1);
        ShowCountdown();
        if (_left == 0)
        {
            _timer.Stop();
        }
    }

    private void ShowCountdown()
    {
        Confirm.IsEnabled = _left == 0;
        Confirm.Content = _left == 0 ? _texts.Get("reset.confirm") : _texts.Get("reset.wait", _left);
    }
}
