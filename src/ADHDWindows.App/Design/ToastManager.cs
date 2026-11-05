using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ADHDWindows.Platform;

namespace ADHDWindows.App.Design;

/// <summary>
/// Показывает уведомления в правом нижнем углу основного экрана. Они не забирают фокус,
/// закрываются сами или по клику, а самое старое уступает место новому.
/// </summary>
public static class ToastManager
{
    private const int MaxVisible = 4;
    private const int Gap = 4;
    private const int ScreenMargin = 8;

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4);
    private static readonly List<Window> Visible = [];

    public static void Show(ToastKind kind, string title, string message, string? icon = null)
    {
        if (Visible.Count == MaxVisible)
        {
            Dismiss(Visible[0]);
        }

        var card = new ToastCard(kind, title, message, icon);
        var root = new PixelRoot { Child = card };
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = null,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Left = SystemParameters.WorkArea.Right,
            Top = SystemParameters.WorkArea.Bottom,
            Content = root,
        };

        window.SourceInitialized += (_, _) => WindowTraits.MakeNonActivating(new WindowInteropHelper(window).Handle);
        window.MouseLeftButtonDown += (_, _) => Dismiss(window);
        window.ContentRendered += (_, _) =>
        {
            Arrange();
            PixelAnimations.SlideIn(card, ToastCard.CardWidth);
        };

        var timer = new DispatcherTimer { Interval = Lifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Dismiss(window);
        };

        Visible.Add(window);
        window.Show();
        timer.Start();
    }

    private static void Dismiss(Window window)
    {
        if (!Visible.Remove(window))
        {
            return;
        }

        Arrange();
        PixelAnimations.FadeOut(window, window.Close);
    }

    private static void Arrange()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom;
        for (var index = Visible.Count - 1; index >= 0; index--)
        {
            var window = Visible[index];
            var unit = window.ActualWidth / ToastCard.CardWidth;
            var margin = ScreenMargin * unit;
            window.Left = area.Right - window.ActualWidth - margin;
            window.Top = bottom - window.ActualHeight - margin;
            bottom -= window.ActualHeight + (Gap * unit);
        }
    }
}
