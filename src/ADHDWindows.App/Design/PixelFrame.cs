using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ADHDWindows.App.Design;

/// <summary>Рамка из спрайта: углы сохраняют размер, края и середина растягиваются.</summary>
public class PixelFrame : Decorator
{
    public static readonly DependencyProperty SpriteProperty = DependencyProperty.Register(
        nameof(Sprite),
        typeof(string),
        typeof(PixelFrame),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding),
        typeof(Thickness),
        typeof(PixelFrame),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));

    private readonly PixelSnap _snap;

    public PixelFrame()
    {
        _snap = new PixelSnap(this);
    }

    public string? Sprite
    {
        get => (string?)GetValue(SpriteProperty);
        set => SetValue(SpriteProperty, value);
    }

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var padding = Padding;
        var horizontal = padding.Left + padding.Right;
        var vertical = padding.Top + padding.Bottom;
        if (Child is null)
        {
            return new Size(horizontal, vertical);
        }

        Child.Measure(new Size(
            Math.Max(0, constraint.Width - horizontal),
            Math.Max(0, constraint.Height - vertical)));
        return new Size(Child.DesiredSize.Width + horizontal, Child.DesiredSize.Height + vertical);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var padding = Padding;
        Child?.Arrange(new Rect(
            padding.Left,
            padding.Top,
            Math.Max(0, arrangeSize.Width - padding.Left - padding.Right),
            Math.Max(0, arrangeSize.Height - padding.Top - padding.Bottom)));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        // Прозрачная заливка делает всю рамку доступной для мыши.
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var assets = PixelAssets.Current;
        var sprite = assets?.Find(Sprite);
        if (sprite is null)
        {
            return;
        }

        var edge = (double)sprite.Slice;
        var width = Math.Max(Math.Round(ActualWidth), edge * 2);
        var height = Math.Max(Math.Round(ActualHeight), edge * 2);
        double[] x = [0, edge, width - edge];
        double[] y = [0, edge, height - edge];
        double[] columns = [edge, width - (edge * 2), edge];
        double[] rows = [edge, height - (edge * 2), edge];
        var pieces = assets!.Slices(sprite);

        var offset = _snap.Take();
        drawing.PushTransform(new TranslateTransform(offset.X, offset.Y));
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                if (columns[column] > 0 && rows[row] > 0)
                {
                    drawing.DrawImage(
                        pieces[(row * 3) + column],
                        new Rect(x[column], y[row], columns[column], rows[row]));
                }
            }
        }

        drawing.Pop();
    }
}
