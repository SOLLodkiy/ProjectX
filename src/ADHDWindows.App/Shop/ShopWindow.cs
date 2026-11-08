using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Upgrades;

namespace ADHDWindows.App.Shop;

/// <summary>
/// Окно магазина: вкладка на каждую ветку, строка на каждое улучшение. Строки обновляются на месте,
/// поэтому покупка не сбрасывает выбранную вкладку и прокрутку.
/// </summary>
internal sealed class ShopWindow : PixelWindow
{
    private const int ContentWidth = 280;
    private const int RowsHeight = 190;

    private readonly TextCatalog _texts;
    private readonly Action<string> _onBuy;
    private readonly PixelText _balance = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TabControl _tabs = new() { Height = RowsHeight + 20 };
    private readonly PixelText _empty;
    private readonly Dictionary<string, ShopRow> _rows = new(StringComparer.Ordinal);
    private string _structure = string.Empty;

    public ShopWindow(TextCatalog texts, Action<string> onBuy)
    {
        _texts = texts;
        _onBuy = onBuy;
        Title = texts.Get("shop.title");
        IconSprite = "shop";

        var layout = new StackPanel { Width = ContentWidth };
        var balanceRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        balanceRow.Children.Add(new PixelImage { Sprite = "coin", Margin = new Thickness(0, 0, 4, 0) });
        balanceRow.Children.Add(_balance);
        layout.Children.Add(balanceRow);

        _empty = new PixelText { Text = texts.Get("shop.empty"), Wrap = true, Visibility = Visibility.Collapsed };
        layout.Children.Add(_empty);
        layout.Children.Add(_tabs);

        TextElement.SetForeground(layout, PixelAssets.Current.Brush("text"));
        Content = layout;
    }

    /// <summary>Строки по кодам улучшений, для проверок.</summary>
    internal IReadOnlyDictionary<string, ShopRow> Rows => _rows;

    internal int BranchCount => _tabs.Items.Count;

    internal void SelectBranch(int index) => _tabs.SelectedIndex = index;

    public void Update(IReadOnlyList<UpgradeShopBranch> branches, long balance)
    {
        var structure = string.Join('|', branches.Select(branch => branch.Id + ":" + string.Join(',', branch.Offers.Select(offer => offer.Upgrade.Id))));
        if (structure != _structure)
        {
            Rebuild(branches);
            _structure = structure;
        }

        foreach (var offer in branches.SelectMany(branch => branch.Offers))
        {
            _rows[offer.Upgrade.Id].Show(offer);
        }

        var empty = branches.All(branch => branch.Offers.Count == 0);
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _tabs.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ShowBalance(balance);
    }

    public void ShowBalance(long balance)
    {
        _balance.Text = _texts.Get("shop.balance", CoinFormat.Short(balance));
        foreach (var row in _rows.Values)
        {
            row.ShowBalance(balance);
        }
    }

    public void Celebrate(string id)
    {
        if (_rows.TryGetValue(id, out var row))
        {
            PixelAnimations.Flash(row);
        }
    }

    public void Refuse(string id)
    {
        if (_rows.TryGetValue(id, out var row))
        {
            PixelAnimations.Shake(row);
        }
    }

    private void Rebuild(IReadOnlyList<UpgradeShopBranch> branches)
    {
        var selected = (_tabs.SelectedItem as TabItem)?.Tag as string;
        _tabs.Items.Clear();
        _rows.Clear();
        foreach (var branch in branches.Where(branch => branch.Offers.Count > 0))
        {
            var list = new StackPanel();
            foreach (var offer in branch.Offers)
            {
                var row = new ShopRow(_texts, offer.Upgrade, _onBuy);
                _rows[offer.Upgrade.Id] = row;
                list.Children.Add(row);
            }

            var tab = new TabItem
            {
                Header = branch.Name,
                Tag = branch.Id,
                Content = new ScrollViewer
                {
                    Content = list,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Focusable = false,
                },
            };
            AutomationProperties.SetAutomationId(tab, "shop.branch:" + branch.Id);
            _tabs.Items.Add(tab);
        }

        _tabs.SelectedItem = _tabs.Items.OfType<TabItem>().FirstOrDefault(tab => (string)tab.Tag == selected)
            ?? _tabs.Items.OfType<TabItem>().FirstOrDefault();
    }
}

/// <summary>Строка улучшения: значок, название с уровнем, описание и цена или чего не хватает.</summary>
internal sealed class ShopRow : PixelFrame
{
    private readonly TextCatalog _texts;
    private readonly UpgradeDefinition _upgrade;
    private readonly PixelImage _icon;
    private readonly PixelText _level;
    private readonly PixelText _status;
    private UpgradeOffer? _offer;

    public ShopRow(TextCatalog texts, UpgradeDefinition upgrade, Action<string> onBuy)
    {
        _texts = texts;
        _upgrade = upgrade;
        Sprite = "inset";
        Padding = new Thickness(4);
        Margin = new Thickness(0, 0, 0, 2);

        Buy = new PixelButton
        {
            Content = texts.Get("shop.buy"),
            Kind = PixelButtonKind.Primary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Focusable = false,
        };
        AutomationProperties.SetAutomationId(Buy, "shop.buy:" + upgrade.Id);
        Buy.Click += (_, _) => onBuy(upgrade.Id);
        DockPanel.SetDock(Buy, Dock.Right);

        _icon = new PixelImage { Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(_icon, Dock.Left);

        var title = new DockPanel();
        _level = new PixelText { Foreground = PixelAssets.Current.Brush("mist"), Margin = new Thickness(4, 0, 0, 0) };
        DockPanel.SetDock(_level, Dock.Right);
        title.Children.Add(_level);
        title.Children.Add(new PixelText { Text = upgrade.Name, Wrap = true });

        _status = new PixelText { Wrap = true, Margin = new Thickness(0, 2, 0, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        if (upgrade.Description.Length > 0)
        {
            text.Children.Add(new PixelText
            {
                Text = upgrade.Description,
                Wrap = true,
                Foreground = PixelAssets.Current.Brush("mist"),
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        text.Children.Add(_status);

        var row = new DockPanel();
        row.Children.Add(Buy);
        row.Children.Add(_icon);
        row.Children.Add(text);
        Child = row;
    }

    public PixelButton Buy { get; }

    public void Show(UpgradeOffer offer)
    {
        _offer = offer;
        _icon.Sprite = offer.IsLocked ? "lock" : _upgrade.Icon;
        _level.Text = _texts.Get("shop.level", offer.Level, _upgrade.MaxLevel);
        Buy.Visibility = offer.IsMaxed ? Visibility.Collapsed : Visibility.Visible;

        if (offer.IsMaxed)
        {
            _status.Text = _texts.Get("shop.maxed");
            _status.Foreground = PixelAssets.Current.Brush("green");
        }
        else if (offer.IsLocked)
        {
            var missing = string.Join(", ", offer.Missing.Select(item => _texts.Get("shop.requirement", item.Name, item.Level)));
            _status.Text = _texts.Get("shop.requires", missing);
            _status.Foreground = PixelAssets.Current.Brush("red");
        }
        else
        {
            _status.Text = _texts.Get("shop.price", CoinFormat.Short(offer.NextPrice!.Value));
            _status.Foreground = PixelAssets.Current.Brush("gold");
        }
    }

    public void ShowBalance(long balance) =>
        Buy.IsEnabled = _offer is { IsMaxed: false, IsLocked: false, NextPrice: { } price } && balance >= price;
}
