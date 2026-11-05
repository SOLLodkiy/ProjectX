using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Locks;
using ADHDWindows.Platform;

namespace ADHDWindows.App.Locks;

/// <summary>
/// Экран покупки. Не забирает фокус: человек мог печатать в другой программе,
/// и случайный пробел или Enter не должен нажать кнопку покупки.
/// </summary>
internal sealed class LockScreenWindow : PixelWindow
{
    private const int ContentWidth = 240;
    private const int RowsHeight = 140;

    private readonly TextCatalog _texts;
    private readonly Action<string> _onBuy;
    private readonly PixelText _balance = new();
    private readonly StackPanel _rows = new();
    private readonly Dictionary<PixelButton, long> _buttons = [];
    private readonly StackPanel _layout = new() { Width = ContentWidth };

    public LockScreenWindow(TextCatalog texts, Action<string> onBuy)
    {
        _texts = texts;
        _onBuy = onBuy;
        Title = texts.Get("lock.title");
        IconSprite = "lock";
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;

        SourceInitialized += (_, _) => WindowTraits.MakeNonActivating(new WindowInteropHelper(this).Handle);

        _layout.Children.Add(new PixelText
        {
            Text = texts.Get("lock.text"),
            Wrap = true,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var balanceRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var coin = new PixelImage { Sprite = "coin", Margin = new Thickness(0, 0, 4, 0) };
        _balance.VerticalAlignment = VerticalAlignment.Center;
        balanceRow.Children.Add(coin);
        balanceRow.Children.Add(_balance);
        _layout.Children.Add(balanceRow);

        _layout.Children.Add(new ScrollViewer
        {
            Content = _rows,
            MaxHeight = RowsHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
        });

        var later = new PixelButton
        {
            Content = texts.Get("lock.later"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
            Focusable = false,
        };
        later.Click += (_, _) => Close();
        _layout.Children.Add(later);

        // Пиксельный текст наследует цвет, а у окна без рамки Windows он чёрный.
        TextElement.SetForeground(_layout, PixelAssets.Current.Brush("text"));
        Content = _layout;
    }

    public void Update(IReadOnlyList<LockOffer> offers, long balance)
    {
        _rows.Children.Clear();
        _buttons.Clear();
        foreach (var offer in offers)
        {
            _rows.Children.Add(BuildRow(offer));
        }

        ShowBalance(balance);
    }

    public void ShowBalance(long balance)
    {
        _balance.Text = _texts.Get("lock.balance", CoinFormat.Short(balance));
        foreach (var (button, price) in _buttons)
        {
            button.IsEnabled = balance >= price;
        }
    }

    /// <summary>Монетки успели потратиться в другом месте, пока открыт экран.</summary>
    public void Refuse() => PixelAnimations.Shake(_layout);

    private PixelFrame BuildRow(LockOffer offer)
    {
        var buy = new PixelButton
        {
            Content = _texts.Get("lock.buy"),
            Kind = PixelButtonKind.Primary,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        AutomationProperties.SetAutomationId(buy, offer.Key);
        buy.Click += (_, _) => _onBuy(offer.Key);
        _buttons[buy] = offer.Price;
        DockPanel.SetDock(buy, Dock.Right);

        var icon = new PixelImage { Sprite = "lock", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        text.Children.Add(new PixelText { Text = offer.Name, Wrap = true });
        text.Children.Add(new PixelText
        {
            Text = _texts.Get("lock.price", CoinFormat.Short(offer.Price)),
            Foreground = PixelAssets.Current.Brush("gold"),
            Margin = new Thickness(0, 2, 0, 0),
        });

        var row = new DockPanel();
        row.Children.Add(buy);
        row.Children.Add(icon);
        row.Children.Add(text);
        return new PixelFrame { Sprite = "inset", Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 2), Child = row };
    }
}
