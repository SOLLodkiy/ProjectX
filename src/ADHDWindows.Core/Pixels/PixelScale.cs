namespace ADHDWindows.Core.Pixels;

/// <summary>Целочисленное масштабирование: сколько пикселей экрана занимает один пиксель арта.</summary>
public static class PixelScale
{
    /// <summary>Обычный размер интерфейса: пиксель арта равен двум пикселям экрана при масштабе 100%.</summary>
    public const int DefaultUiScale = 2;

    /// <param name="dpiScale">Масштаб монитора: 1 при 100%, 1,5 при 150%.</param>
    /// <param name="uiScale">Размер интерфейса при 100%.</param>
    public static int DevicePixels(double dpiScale, int uiScale = DefaultUiScale) =>
        Math.Max(1, (int)Math.Round(dpiScale * uiScale, MidpointRounding.AwayFromZero));

    /// <summary>Коэффициент для WPF, где размеры заданы в единицах 1/96 дюйма.</summary>
    public static double LayoutFactor(double dpiScale, int uiScale = DefaultUiScale) =>
        DevicePixels(dpiScale, uiScale) / dpiScale;
}
