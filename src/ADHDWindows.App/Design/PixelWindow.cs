using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using ADHDWindows.Core.Pixels;
using ADHDWindows.Platform;

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

        // Окно ещё не показано: на основном экране оно не мелькнёт.
        if (TestScreen.Corner() is { } corner)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (_, _) => WindowTraits.Move(new WindowInteropHelper(this).Handle, corner);
        }

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
