using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ADHDWindows.App.Design;

/// <summary>
/// Анимации интерфейса. Значения меняются ступенями и на целое число пикселей арта,
/// поэтому в покое и в движении картинка остаётся на сетке.
/// </summary>
public static class PixelAnimations
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(40);

    /// <summary>Тряска: короткие сдвиги в стороны.</summary>
    public static void Shake(UIElement element, int amplitude = 2)
    {
        int[] offsets = [-amplitude, amplitude, -amplitude, amplitude, -amplitude / 2, amplitude / 2, 0];
        Translate(element).BeginAnimation(TranslateTransform.XProperty, Steps(offsets.Select(value => (double)value)));
    }

    /// <summary>Вспышка: элемент дважды мигает.</summary>
    public static void Flash(UIElement element)
    {
        double[] values = [0.25, 1, 0.25, 1];
        element.BeginAnimation(UIElement.OpacityProperty, Steps(values, TimeSpan.FromMilliseconds(70)));
    }

    /// <summary>Мягкое появление в четыре ступени.</summary>
    public static void FadeIn(UIElement element)
    {
        double[] values = [0.25, 0.5, 0.75, 1];
        element.BeginAnimation(UIElement.OpacityProperty, Steps(values));
    }

    /// <summary>Мягкое исчезновение, после него вызывается <paramref name="completed"/>.</summary>
    public static void FadeOut(UIElement element, Action completed)
    {
        double[] values = [0.75, 0.5, 0.25, 0];
        var animation = Steps(values);
        animation.Completed += (_, _) => completed();
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>Въезд со смещения <paramref name="distance"/> по горизонтали на своё место.</summary>
    public static void SlideIn(UIElement element, int distance)
    {
        var values = Enumerable.Range(1, 8).Select(step => (double)(distance * (8 - step) / 8));
        Translate(element).BeginAnimation(TranslateTransform.XProperty, Steps(values));
    }

    /// <summary>Вылет спрайта вверх с исчезновением: так показывается начисление.</summary>
    public static void FlyUp(Canvas layer, string sprite, Point from, int height = 24)
    {
        var image = new PixelImage { Sprite = sprite, IsHitTestVisible = false };
        Canvas.SetLeft(image, Math.Round(from.X));
        Canvas.SetTop(image, Math.Round(from.Y));
        layer.Children.Add(image);

        var rise = Enumerable.Range(1, 8).Select(step => (double)(-height * step / 8));
        Translate(image).BeginAnimation(TranslateTransform.YProperty, Steps(rise));

        double[] fade = [1, 1, 1, 1, 1, 0.75, 0.5, 0];
        var animation = Steps(fade);
        animation.Completed += (_, _) => layer.Children.Remove(image);
        image.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static TranslateTransform Translate(UIElement element)
    {
        if (element.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }

        return transform;
    }

    private static DoubleAnimationUsingKeyFrames Steps(IEnumerable<double> values, TimeSpan? step = null)
    {
        var duration = step ?? Step;
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        var time = TimeSpan.Zero;
        foreach (var value in values)
        {
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(value, KeyTime.FromTimeSpan(time)));
            time += duration;
        }

        animation.Duration = time;
        return animation;
    }
}
