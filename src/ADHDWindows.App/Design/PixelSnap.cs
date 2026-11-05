using System.Windows;
using System.Windows.Media;

namespace ADHDWindows.App.Design;

/// <summary>
/// Прижимает рисунок элемента к сетке арта. Раскладка WPF может поставить элемент между пикселями арта,
/// например при центрировании, и тогда картинка размылась бы или разошлась с соседями.
/// </summary>
internal sealed class PixelSnap
{
    private const double Tolerance = 0.001;

    private readonly FrameworkElement _owner;
    private Vector _rendered;

    public PixelSnap(FrameworkElement owner)
    {
        _owner = owner;
        // Элемент рисуется ещё до того, как родители займут своё место, поэтому положение проверяется после раскладки.
        owner.LayoutUpdated += OnLayoutUpdated;
    }

    /// <summary>Смещение для текущего рисования, в пикселях арта.</summary>
    public Vector Take()
    {
        _rendered = Compute();
        return _rendered;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if ((Compute() - _rendered).LengthSquared > Tolerance * Tolerance)
        {
            _owner.InvalidateVisual();
        }
    }

    private Vector Compute()
    {
        for (var visual = VisualTreeHelper.GetParent(_owner); visual is not null; visual = VisualTreeHelper.GetParent(visual))
        {
            if (visual is PixelRoot root)
            {
                // Внутри корня координаты уже заданы в пикселях арта.
                var origin = _owner.TransformToAncestor(root).Transform(default);
                return new Vector(Remainder(origin.X), Remainder(origin.Y));
            }
        }

        return default;
    }

    // Округление всегда вверх от половины, чтобы родитель и его дети сдвигались одинаково.
    private static double Remainder(double position) => Math.Floor(position + 0.5) - position;
}
