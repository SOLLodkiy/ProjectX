using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ADHDWindows.Core.Pixels;

namespace ADHDWindows.App.Design;

/// <summary>Окно программы: пиксельная рамка, заголовок и мягкое появление.</summary>
public class PixelWindow : Window
{
    public static readonly DependencyProperty UiScaleProperty = DependencyProperty.Register(
        nameof(UiScale), typeof(int), typeof(PixelWindow), new FrameworkPropertyMetadata(PixelScale.DefaultUiScale));

    public static readonly DependencyProperty IconSpriteProperty = DependencyProperty.Register(
        nameof(IconSprite), typeof(string), typeof(PixelWindow), new FrameworkPropertyMetadata("app_icon"));

    public PixelWindow()
    {
        SetResourceReference(StyleProperty, typeof(PixelWindow));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ContentRendered += (_, _) =>
        {
            if (GetVisualChild(0) is FrameworkElement root)
            {
                PixelAnimations.FadeIn(root);
            }
        };
    }

    public int UiScale
    {
        get => (int)GetValue(UiScaleProperty);
        set => SetValue(UiScaleProperty, value);
    }

    public string? IconSprite
    {
        get => (string?)GetValue(IconSpriteProperty);
        set => SetValue(IconSpriteProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_TitleBar") is UIElement titleBar)
        {
            titleBar.MouseLeftButtonDown += OnTitleBarPressed;
        }

        if (GetTemplateChild("PART_Close") is ButtonBase close)
        {
            close.Click += (_, _) => Close();
        }
    }

    private void OnTitleBarPressed(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
