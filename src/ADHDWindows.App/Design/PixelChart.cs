using System.Windows;
using System.Windows.Media;

namespace ADHDWindows.App.Design;

/// <summary>
/// Столбчатый график в пикселях арта: столбики целой ширины и высоты, без сглаживания.
/// Последний столбик выделен цветом: это текущий день или неделя.
/// </summary>
public sealed class PixelBarChart : FrameworkElement
{
    /// <summary>Промежуток между столбиками.</summary>
    public const int Gap = 2;

    private readonly PixelSnap _snap;
    private IReadOnlyList<long> _values = [];

    public PixelBarChart()
    {
        _snap = new PixelSnap(this);
    }

    public IReadOnlyList<long> Values
    {
        get => _values;
        set
        {
            _values = value;
            InvalidateVisual();
        }
    }

    /// <summary>Ширина места под один столбик, чтобы подписи под графиком встали ровно под столбиками.</summary>
    public int SlotWidth => _values.Count == 0 ? 0 : (int)Math.Floor(ActualWidth) / _values.Count;

    /// <summary>Высота столбика в пикселях арта: ненулевое значение видно хотя бы одной строкой.</summary>
    public static int BarHeight(long value, long max, int height)
    {
        if (value <= 0 || max <= 0 || height <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round((double)value / max * height), 1, height);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : Math.Floor(availableSize.Width), Height is var height && double.IsNaN(height) ? 40 : height);

    protected override void OnRender(DrawingContext drawing)
    {
        var height = (int)Math.Floor(ActualHeight);
        var slot = SlotWidth;
        if (slot <= Gap || height < 2)
        {
            return;
        }

        var offset = _snap.Take();
        drawing.PushTransform(new TranslateTransform(offset.X, offset.Y));
        var assets = PixelAssets.Current;
        var max = _values.Count == 0 ? 0 : _values.Max();
        var chartHeight = height - 1;

        // Основание графика на всю ширину.
        drawing.DrawRectangle(assets.Brush("line"), null, new Rect(0, chartHeight, slot * _values.Count, 1));
        for (var index = 0; index < _values.Count; index++)
        {
            var bar = BarHeight(_values[index], max, chartHeight);
            var left = (index * slot) + (Gap / 2);
            var width = slot - Gap;
            if (bar == 0)
            {
                continue;
            }

            var last = index == _values.Count - 1;
            drawing.DrawRectangle(assets.Brush(last ? "green" : "gold"), null, new Rect(left, chartHeight - bar, width, bar));

            // Тень справа и блик сверху придают столбику объём, как у спрайтов.
            if (width > 2)
            {
                drawing.DrawRectangle(assets.Brush(last ? "greenDark" : "amber"), null, new Rect(left + width - 1, chartHeight - bar, 1, bar));
            }

            if (bar > 1)
            {
                drawing.DrawRectangle(assets.Brush(last ? "text" : "white"), null, new Rect(left, chartHeight - bar, Math.Max(1, width - 1), 1));
            }
        }

        drawing.Pop();
    }
}

/// <summary>Горизонтальная шкала доли от 0 до 1: заполненная часть целой ширины.</summary>
public sealed class PixelMeter : FrameworkElement
{
    private readonly PixelSnap _snap;
    private double _share;

    public PixelMeter()
    {
        _snap = new PixelSnap(this);
        Height = 4;
    }

    public double Share
    {
        get => _share;
        set
        {
            _share = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
            InvalidateVisual();
        }
    }

    /// <summary>Цвет заполнения из палитры.</summary>
    public string Color { get; set; } = "gold";

    /// <summary>Заполненная ширина в пикселях арта: ненулевая доля видна хотя бы одним пикселем.</summary>
    public static int Filled(double share, int width) =>
        share <= 0 || width <= 0 ? 0 : Math.Clamp((int)Math.Round(share * width), 1, width);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : Math.Floor(availableSize.Width), Height);

    protected override void OnRender(DrawingContext drawing)
    {
        var width = (int)Math.Floor(ActualWidth);
        var height = (int)Math.Floor(ActualHeight);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var offset = _snap.Take();
        drawing.PushTransform(new TranslateTransform(offset.X, offset.Y));
        var assets = PixelAssets.Current;
        drawing.DrawRectangle(assets.Brush("night"), null, new Rect(0, 0, width, height));
        var filled = Filled(_share, width);
        if (filled > 0)
        {
            drawing.DrawRectangle(assets.Brush(Color), null, new Rect(0, 0, filled, height));
        }

        drawing.Pop();
    }
}
