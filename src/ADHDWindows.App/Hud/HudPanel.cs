using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;

namespace ADHDWindows.App.Hud;

/// <summary>Содержимое мини-панели HUD.</summary>
public sealed class HudPanel : PixelFrame
{
    public const int PanelWidth = 150;

    private readonly TextCatalog _texts;
    private readonly PixelText _balance;
    private readonly PixelText _rate;
    private readonly PixelText _hardship;
    private readonly PixelText _quest;

    public HudPanel(TextCatalog texts)
    {
        _texts = texts;
        Sprite = "panel";
        Padding = new Thickness(5);
        Width = PanelWidth;

        var rows = new StackPanel();
        _balance = AddRow(rows, "coin", "hud.balance");
        _rate = AddRow(rows, "chart", "hud.rate");
        _hardship = AddTextRow(rows, "warning", "hud.hardship");
        _quest = AddTextRow(rows, "quest", "hud.quest");

        var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 3, 0, 0) };
        Shop = new PixelButton { Content = texts.Get("hud.shop"), IsEnabled = false };
        Logs = new PixelButton { Content = texts.Get("hud.logs"), IsEnabled = false, Margin = new Thickness(3, 0, 0, 0) };
        buttons.Children.Add(Shop);
        buttons.Children.Add(Logs);
        rows.Children.Add(buttons);
        Child = rows;
    }

    /// <summary>Открывает окно магазина, подключается через <see cref="HudWindow.ConnectShop"/>.</summary>
    public PixelButton Shop { get; }

    /// <summary>Откроется в окне логов. Подключается вместе с ним.</summary>
    public PixelButton Logs { get; }

    public void Show(long balance, long perMinute, string? hardship, string? quest)
    {
        _balance.Text = balance.ToString(CultureInfo.InvariantCulture);
        _rate.Text = CoinFormat.Gain(perMinute);
        _hardship.Text = hardship ?? _texts.Get("hud.none");
        _quest.Text = quest ?? _texts.Get("hud.none");
    }

    /// <summary>Строка с числом: подпись слева, значение справа.</summary>
    private PixelText AddRow(StackPanel rows, string icon, string label)
    {
        var row = StartRow(rows, icon);
        var name = Label(label);
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);

        var value = new PixelText { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(value);
        return value;
    }

    /// <summary>Строка с названием: значение идёт под подписью, потому что может быть длинным.</summary>
    private PixelText AddTextRow(StackPanel rows, string icon, string label)
    {
        var row = StartRow(rows, icon);
        var value = new PixelText { Wrap = true };
        row.Children.Add(new StackPanel { Children = { Label(label), value } });
        return value;
    }

    private static DockPanel StartRow(StackPanel rows, string icon)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), LastChildFill = true };
        var image = new PixelImage { Sprite = icon, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(image, Dock.Left);
        row.Children.Add(image);
        rows.Children.Add(row);
        return row;
    }

    private PixelText Label(string key) => new()
    {
        Text = _texts.Get(key),
        Foreground = PixelAssets.Current.Brush("mist"),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
