using System.Windows;
using System.Windows.Interop;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hud;
using ADHDWindows.Platform;

namespace ADHDWindows.App.Hud;

/// <summary>
/// Окно мини-панели. Отдельное от полоски, потому что принимает клики, а полоска их пропускает.
/// </summary>
internal sealed class HudPanelWindow : Window
{
    private const int Gap = 2;

    private readonly PixelRoot _root;

    public HudPanelWindow(HudPanel panel)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = null;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        _root = new PixelRoot { Child = panel };
        Content = _root;
        SourceInitialized += (_, _) => WindowTraits.MakeNonActivating(Handle);
    }

    private IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>Границы окна на экране.</summary>
    public ScreenRect Bounds => WindowTraits.Bounds(Handle);

    /// <summary>Показывает панель рядом с полоской на том же мониторе.</summary>
    public void Open(ScreenRect bar, int uiScale, IReadOnlyList<ScreenArea> screens)
    {
        _root.UiScale = uiScale;

        // Размер зависит от монитора, поэтому окно сначала попадает на него.
        WindowTraits.Move(Handle, new ScreenPoint(bar.Left, bar.Top));
        Show();
        UpdateLayout();

        var size = Bounds;
        var gap = Gap * _root.DevicePixels;
        var origin = HudPlacement.PanelOrigin(bar, size.Width, size.Height, gap, screens);
        WindowTraits.Move(Handle, origin);
    }
}
