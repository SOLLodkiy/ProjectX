using System.Globalization;
using ADHDWindows.Core.Display;

namespace ADHDWindows.Core.Hud;

/// <summary>Расчёт положения HUD на мониторах. Все величины в пикселях экрана.</summary>
public static class HudPlacement
{
    /// <summary>Строка конфигурации мониторов. Порядок перечисления не влияет на результат.</summary>
    public static string ConfigurationKey(IEnumerable<ScreenArea> screens) =>
        string.Join(
            ";",
            screens
                .Select(screen => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{screen.Bounds.Left},{screen.Bounds.Top},{screen.Bounds.Width}x{screen.Bounds.Height}@{screen.Dpi}"))
                .Order(StringComparer.Ordinal));

    /// <summary>Правый верхний угол рабочей области основного монитора.</summary>
    public static ScreenPoint Default(int width, int height, int margin, IReadOnlyList<ScreenArea> screens)
    {
        if (screens.Count == 0)
        {
            return new ScreenPoint(margin, margin);
        }

        var area = (screens.FirstOrDefault(screen => screen.IsPrimary) ?? screens[0]).WorkArea;
        return Keep(new ScreenRect(area.Right - width - margin, area.Top + margin, width, height), screens);
    }

    /// <summary>
    /// Точка, в которой прямоугольник целиком лежит на одном мониторе. Если он уже так лежит, точка не меняется,
    /// иначе он сдвигается на монитор с наибольшим пересечением или на ближайший.
    /// </summary>
    public static ScreenPoint Keep(ScreenRect rect, IReadOnlyList<ScreenArea> screens)
    {
        if (screens.Count == 0)
        {
            return new ScreenPoint(rect.Left, rect.Top);
        }

        var area = AreaOf(rect, screens);
        return new ScreenPoint(
            Limit(rect.Left, area.Left, area.Right - rect.Width),
            Limit(rect.Top, area.Top, area.Bottom - rect.Height));
    }

    /// <summary>Положение панели: под полоской HUD, а если там нет места, над ней.</summary>
    public static ScreenPoint PanelOrigin(ScreenRect bar, int width, int height, int gap, IReadOnlyList<ScreenArea> screens)
    {
        var top = bar.Bottom + gap;
        if (screens.Count > 0 && top + height > AreaOf(bar, screens).Bottom)
        {
            top = bar.Top - gap - height;
        }

        return Keep(new ScreenRect(bar.Left, top, width, height), screens);
    }

    /// <summary>Монетка летит вниз, если над полоской HUD нет места для полёта.</summary>
    public static bool FliesDown(ScreenRect bar, int flight, IReadOnlyList<ScreenArea> screens) =>
        screens.Count > 0 && bar.Top - flight < AreaOf(bar, screens).Top;

    private static ScreenRect AreaOf(ScreenRect rect, IReadOnlyList<ScreenArea> screens)
    {
        foreach (var screen in screens)
        {
            if (screen.WorkArea.Contains(rect))
            {
                return screen.WorkArea;
            }
        }

        return screens
            .OrderByDescending(screen => screen.WorkArea.Overlap(rect))
            .ThenBy(screen => screen.WorkArea.DistanceTo(rect))
            .First()
            .WorkArea;
    }

    // Math.Clamp запрещает минимум больше максимума, а прямоугольник может быть больше монитора.
    private static int Limit(int value, int low, int high) => Math.Max(low, Math.Min(value, high));
}
