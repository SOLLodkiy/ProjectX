using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ADHDWindows.Core.Pixels;

namespace ADHDWindows.App.Design;

/// <summary>
/// Корень пиксельного интерфейса. Внутри него размеры заданы в пикселях арта, а один пиксель арта
/// всегда занимает целое число пикселей экрана на любом масштабе монитора.
/// </summary>
public class PixelRoot : Decorator
{
    public static readonly DependencyProperty UiScaleProperty = DependencyProperty.Register(
        nameof(UiScale),
        typeof(int),
        typeof(PixelRoot),
        new FrameworkPropertyMetadata(PixelScale.DefaultUiScale, (element, _) => ((PixelRoot)element).UpdateScale()));

    private readonly ScaleTransform _scale = new();

    public PixelRoot()
    {
        LayoutTransform = _scale;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SetResourceReference(TextElement.ForegroundProperty, "Brush.text");
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        Loaded += (_, _) => UpdateScale();
        UpdateScale();
    }

    public int UiScale
    {
        get => (int)GetValue(UiScaleProperty);
        set => SetValue(UiScaleProperty, value);
    }

    /// <summary>Сколько пикселей экрана занимает один пиксель арта.</summary>
    public int DevicePixels => PixelScale.DevicePixels(VisualTreeHelper.GetDpi(this).DpiScaleX, UiScale);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateScale();
    }

    /// <summary>Пересчитывает масштаб. На экране вызывается сам при смене монитора.</summary>
    public void UpdateScale()
    {
        var factor = PixelScale.LayoutFactor(VisualTreeHelper.GetDpi(this).DpiScaleX, UiScale);
        _scale.ScaleX = factor;
        _scale.ScaleY = factor;
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Перетаскивание ползунка даёт дробную прокрутку, а содержимое должно стоять на сетке.
        var offset = Math.Round(e.VerticalOffset);
        if (offset != e.VerticalOffset && e.OriginalSource is ScrollViewer viewer)
        {
            viewer.ScrollToVerticalOffset(offset);
        }
    }
}
