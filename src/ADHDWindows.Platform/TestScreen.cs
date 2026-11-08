using System.Globalization;
using ADHDWindows.Core.Display;

namespace ADHDWindows.Platform;

/// <summary>
/// Монитор для окон автотестов, чтобы они не мешали человеку на основном экране.
/// Переменная окружения содержит точку «x,y» в пикселях экрана, окна открываются на мониторе с этой точкой.
/// Без переменной окна открываются как обычно.
/// </summary>
public static class TestScreen
{
    public const string Variable = "ADHDWINDOWS_TEST_SCREEN";

    /// <summary>Отступ окна от угла рабочей области монитора в пикселях экрана.</summary>
    public const int Margin = 48;

    /// <summary>Монитор для окон или null, если переменная не задана или такого монитора нет.</summary>
    public static ScreenArea? Find()
    {
        var value = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            return null;
        }

        return Desktop.Screens().FirstOrDefault(screen => screen.Bounds.Contains(new ScreenPoint(x, y)));
    }

    /// <summary>Левый верхний угол для окна на тестовом мониторе.</summary>
    public static ScreenPoint? Corner() =>
        Find() is { } screen ? new ScreenPoint(screen.WorkArea.Left + Margin, screen.WorkArea.Top + Margin) : null;
}
