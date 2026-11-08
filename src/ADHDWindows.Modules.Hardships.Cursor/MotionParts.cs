using ADHDWindows.Core.Display;

namespace ADHDWindows.Modules.Hardships.Cursor;

/// <summary>Хрупкий курсор: если вести его слишком быстро, он «разбивается» и возвращается туда, где был недавно.</summary>
public sealed class GlassGuard(double limit = GlassGuard.DefaultLimit)
{
    /// <summary>Предельная скорость в пикселях в секунду.</summary>
    public const double DefaultLimit = 2600;

    /// <summary>За какое время меряется скорость.</summary>
    public const double Window = 0.1;

    /// <summary>Насколько назад во времени возвращается курсор.</summary>
    public const double Rewind = 0.5;

    public const double Cooldown = 0.4;

    private readonly LinkedList<(double Time, ScreenPoint Point)> _history = [];
    private double _calmUntil;

    public int Breaks { get; private set; }

    /// <summary>Записывает положение. Возвращает точку, куда вернуть курсор, если он разбился.</summary>
    public ScreenPoint? Step(double now, ScreenPoint position)
    {
        _history.AddLast((now, position));
        while (_history.First is { } first && now - first.Value.Time > Rewind + Window)
        {
            _history.RemoveFirst();
        }

        if (now < _calmUntil)
        {
            return null;
        }

        var recent = _history.LastOrDefault(item => now - item.Time >= Window);
        if (recent == default)
        {
            return null;
        }

        var dx = position.X - recent.Point.X;
        var dy = position.Y - recent.Point.Y;
        var speed = Math.Sqrt((dx * dx) + (dy * dy)) / Math.Max(Window, now - recent.Time);
        if (speed <= limit)
        {
            return null;
        }

        var back = _history.First!.Value.Point;
        _history.Clear();
        _history.AddLast((now, back));
        _calmUntil = now + Cooldown;
        Breaks++;
        return back;
    }

    /// <summary>Кнопка мыши нажата: история начинается заново, чтобы не вернуть курсор посреди перетаскивания.</summary>
    public void Reset() => _history.Clear();
}

/// <summary>Задержка: курсор показывает, где его вели <see cref="Delay"/> секунд назад.</summary>
public sealed class LagLine(double delay = LagLine.DefaultDelay)
{
    public const double DefaultDelay = 0.25;

    private readonly Queue<(double Time, ScreenPoint Point)> _points = new();
    private ScreenPoint _shown;

    public double Delay => delay;

    public void Add(double now, ScreenPoint point) => _points.Enqueue((now, point));

    /// <summary>Куда поставить курсор сейчас.</summary>
    public ScreenPoint At(double now)
    {
        while (_points.Count > 0 && now - _points.Peek().Time >= delay)
        {
            _shown = _points.Dequeue().Point;
        }

        return _shown;
    }

    public void Start(ScreenPoint point)
    {
        _points.Clear();
        _shown = point;
    }
}

/// <summary>Мониторы для движущихся усложнений.</summary>
public static class ScreenBounds
{
    /// <summary>Монитор с точкой или ближайший к ней.</summary>
    public static ScreenRect Of(IReadOnlyList<ScreenArea> screens, ScreenPoint point)
    {
        if (screens.Count == 0)
        {
            return new ScreenRect(0, 0, 1920, 1080);
        }

        var spot = new ScreenRect(point.X, point.Y, 1, 1);
        return screens.FirstOrDefault(screen => screen.Bounds.Contains(point))?.Bounds
            ?? screens.MinBy(screen => screen.Bounds.DistanceTo(spot))!.Bounds;
    }

    /// <summary>Точка, сдвинутая внутрь ближайшего монитора: курсор не уходит в промежутки между мониторами.</summary>
    public static ScreenPoint Clamp(IReadOnlyList<ScreenArea> screens, ScreenPoint point)
    {
        var screen = Of(screens, point);
        return new ScreenPoint(
            Math.Clamp(point.X, screen.Left, screen.Right - 1),
            Math.Clamp(point.Y, screen.Top, screen.Bottom - 1));
    }
}
