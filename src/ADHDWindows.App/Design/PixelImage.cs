using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ADHDWindows.Core.Pixels;

namespace ADHDWindows.App.Design;

/// <summary>Спрайт из атласа с целым увеличением. Спрайт с кадрами и fps проигрывается сам.</summary>
public class PixelImage : FrameworkElement
{
    public static readonly DependencyProperty SpriteProperty = DependencyProperty.Register(
        nameof(Sprite),
        typeof(string),
        typeof(PixelImage),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            (element, _) => ((PixelImage)element).Restart()));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom),
        typeof(int),
        typeof(PixelImage),
        new FrameworkPropertyMetadata(
            1,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly PixelSnap _snap;
    private DispatcherTimer? _timer;
    private int _frame;

    public PixelImage()
    {
        _snap = new PixelSnap(this);
        Loaded += (_, _) => Restart();
        Unloaded += (_, _) => Stop();
    }

    public string? Sprite
    {
        get => (string?)GetValue(SpriteProperty);
        set => SetValue(SpriteProperty, value);
    }

    public int Zoom
    {
        get => (int)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    private AtlasSprite? Current => PixelAssets.Current?.Find(Sprite);

    protected override Size MeasureOverride(Size availableSize) =>
        Current is { } sprite ? new Size(sprite.Width * Zoom, sprite.Height * Zoom) : default;

    protected override void OnRender(DrawingContext drawing)
    {
        if (Current is not { } sprite)
        {
            return;
        }

        var offset = _snap.Take();
        var frame = PixelAssets.Current.Frame(sprite, _frame % sprite.Frames);
        drawing.DrawImage(frame, new Rect(offset.X, offset.Y, sprite.Width * Zoom, sprite.Height * Zoom));
    }

    private void Restart()
    {
        Stop();
        _frame = 0;
        if (IsLoaded && Current is { Frames: > 1, Fps: > 0 } sprite)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1.0 / sprite.Fps),
            };
            _timer.Tick += (_, _) =>
            {
                _frame++;
                InvalidateVisual();
            };
            _timer.Start();
        }
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
}
