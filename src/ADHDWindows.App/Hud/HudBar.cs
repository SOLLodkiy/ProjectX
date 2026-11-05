using System.Windows;
using System.Windows.Controls;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Economy;

namespace ADHDWindows.App.Hud;

/// <summary>Полоска HUD: ручка, монетка и баланс.</summary>
public sealed class HudBar : PixelFrame
{
    public const int BarWidth = 76;
    public const int BarHeight = 22;

    /// <summary>Ширина левой части полоски, за которую она перетаскивается.</summary>
    public const int HandleWidth = 11;

    private readonly PixelText _balance;

    public HudBar()
    {
        Sprite = "panel";
        Padding = new Thickness(3);
        Width = BarWidth;
        Height = BarHeight;

        var row = new DockPanel { LastChildFill = true };
        var grip = new PixelImage { Sprite = "grip", Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(grip, Dock.Left);
        row.Children.Add(grip);

        var coin = new PixelImage { Sprite = "coin", VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(coin, Dock.Left);
        row.Children.Add(coin);

        _balance = new PixelText
        {
            Shadow = true,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = PixelAssets.Current.Brush("gold"),
        };
        row.Children.Add(_balance);
        Child = row;
    }

    public void ShowBalance(long balance) => _balance.Text = CoinFormat.Short(balance);
}
