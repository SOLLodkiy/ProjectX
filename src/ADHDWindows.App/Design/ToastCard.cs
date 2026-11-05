using System.Windows;
using System.Windows.Controls;

namespace ADHDWindows.App.Design;

public enum ToastKind
{
    Info,
    Reward,
    Success,
    Danger,
}

/// <summary>Содержимое всплывающего уведомления. Показывается через <see cref="ToastManager"/>.</summary>
public sealed class ToastCard : PixelFrame
{
    public const int CardWidth = 180;

    public ToastCard(ToastKind kind, string title, string message, string? icon = null)
    {
        Sprite = kind switch
        {
            ToastKind.Reward => "toast_reward",
            ToastKind.Success => "toast_success",
            ToastKind.Danger => "toast_danger",
            _ => "toast_info",
        };
        Padding = new Thickness(6);
        Width = CardWidth;

        var row = new DockPanel();
        if (PixelAssets.Current.Find(icon) is not null)
        {
            var image = new PixelImage { Sprite = icon, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(image, Dock.Left);
            row.Children.Add(image);
        }

        var texts = new StackPanel();
        texts.Children.Add(new PixelText
        {
            Text = title,
            Wrap = true,
            Shadow = true,
            Foreground = PixelAssets.Current.Brush("gold"),
        });
        texts.Children.Add(new PixelText
        {
            Text = message,
            Wrap = true,
            Margin = new Thickness(0, 2, 0, 0),
        });
        row.Children.Add(texts);
        Child = row;
    }
}
