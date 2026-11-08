using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Physics;

/// <summary>
/// Сугроб с лопатой: снег засыпает все мониторы, кроме круга вокруг курсора. Курсор не заходит в снег, а клики,
/// упёршиеся в снег, не доходят до программ и не приносят монеток. Рядом лежит лопата: её берут зажатой левой
/// кнопкой за ручку, водят и бросают, и штык расчищает снег.
/// </summary>
public sealed class SnowdriftHardship : OverlayHardship
{
    public const int BaseCell = 8;
    public const double BaseRadius = 120;
    public const double BaseShovel = 120;
    public const uint SnowColor = 0xF4F8FF;
    public const byte SnowAlpha = 246;
    public const int ArtScale = 2;

    private const int LeftDown = 0x0201;
    private const int LeftUp = 0x0202;

    private readonly Lock _lock = new();
    private readonly HashSet<int> _swallowedUps = [];
    private SnowField? _snow;
    private Shovel? _shovel;
    private MouseMoveFilter? _filter;
    private RegionOverlay? _snowWindow;
    private SpriteOverlay? _shovelSprite;
    private ScreenRect _area;
    private int _shownVersion = -1;
    private int _blockedClicks;
    private int _cleared;

    public override HardshipInfo Info { get; } = new(
        "snowdrift",
        HardshipLevel.Medium,
        HardshipResources.CursorMotion | HardshipResources.Screen | HardshipResources.MouseButtons);

    /// <summary>Сугроб, для проверок.</summary>
    internal SnowField? Snow
    {
        get
        {
            lock (_lock)
            {
                return _snow;
            }
        }
    }

    /// <summary>Лопата, для проверок.</summary>
    internal Shovel? Shovel
    {
        get
        {
            lock (_lock)
            {
                return _shovel;
            }
        }
    }

    /// <summary>Сколько кликов упёрлось в снег.</summary>
    internal int BlockedClicks
    {
        get
        {
            lock (_lock)
            {
                return _blockedClicks;
            }
        }
    }

    protected override void Begin()
    {
        var spread = new Spread(new Random());
        var cursor = Vec.From(CursorControl.Position());
        lock (_lock)
        {
            _area = PhysicsArea.All();
            _snow = new SnowField(_area, spread.Vary(BaseCell));
            _snow.ClearCircle(cursor, spread.Vary(BaseRadius));
            var length = spread.Vary(BaseShovel);
            _shovel = new Shovel(
                PhysicsArea.Clamp(_area, cursor + new Vec(-length / 10, length * 0.45)),
                0,
                length,
                bladeLength: length / 3,
                bladeWidth: length * 0.38);
            _swallowedUps.Clear();
            _blockedClicks = 0;
            _cleared = 0;
            _shownVersion = -1;
        }

        base.Begin();
        _filter = new MouseMoveFilter(Confine, Context.Log) { Buttons = OnButton };
        if (!_filter.Start())
        {
            throw new InvalidOperationException("Фильтр мыши для сугроба не поставлен");
        }
    }

    protected override void End()
    {
        _filter?.Dispose();
        _filter = null;
        base.End();
    }

    protected override void Create()
    {
        _snowWindow = Overlay.CreateRegion(SnowColor, SnowAlpha);
        _shovelSprite = Overlay.CreateSprite(null);
    }

    protected override void Tick(double seconds)
    {
        List<ScreenRect>? shape = null;
        Shovel shovel;
        lock (_lock)
        {
            if (_snow is null || _shovel is null)
            {
                return;
            }

            _cleared += _shovel.Step(seconds, Vec.From(CursorControl.Position()), _snow, _area);
            if (_snow.Version != _shownVersion)
            {
                _shownVersion = _snow.Version;
                shape = _snow.Rects();
            }

            shovel = _shovel;
        }

        if (shape is not null)
        {
            _snowWindow?.SetShape(shape);
        }

        var (picture, origin) = ShovelArt.Render(shovel, ArtScale);
        _shovelSprite?.Show(picture.Pixels, picture.Width, picture.Height, ArtScale, origin);
    }

    protected override void Finish()
    {
        lock (_lock)
        {
            Context.Log.Info($"Сугроб: расчищено клеток {_cleared}, кликов в снег {_blockedClicks}");
            _snow = null;
            _shovel = null;
        }

        _snowWindow = null;
        _shovelSprite = null;
    }

    /// <summary>Решение фильтра движения без хука, для проверок.</summary>
    internal ScreenPoint? Confine(ScreenPoint current, ScreenPoint target)
    {
        lock (_lock)
        {
            if (_snow is null || !_snow.IsSnow(target) && _snow.Reach(Vec.From(current), Vec.From(target)) == Vec.From(target))
            {
                return null;
            }

            // Курсор, оказавшийся в снегу (его поставила программа), может из него выйти.
            if (_snow.IsSnow(current))
            {
                return null;
            }

            // Курсор упирается в снег и скользит вдоль стенки по свободной оси.
            var from = Vec.From(current);
            var direct = _snow.Reach(from, Vec.From(target));
            var alongX = _snow.Reach(from, new Vec(target.X, current.Y));
            var alongY = _snow.Reach(from, new Vec(current.X, target.Y));
            var best = new[] { direct, alongX, alongY }.MaxBy(point => (point - from).Length);
            return best.ToPoint();
        }
    }

    /// <summary>
    /// Решение фильтра кнопок без хука, для проверок: true — событие проглочено. Отпускание глотается, только если
    /// проглочено последнее нажатие той же кнопки: так кнопка не может «залипнуть» в системе.
    /// </summary>
    internal bool OnButton(int message, ScreenPoint point)
    {
        lock (_lock)
        {
            if (_snow is null || _shovel is null)
            {
                return false;
            }

            if (message is not (LeftDown or 0x0204 or 0x0207))
            {
                if (message == LeftUp && _shovel.IsHeld)
                {
                    _shovel.Release();
                }

                return _swallowedUps.Remove(message);
            }

            if (message == LeftDown && _shovel.HitsHandle(Vec.From(point)))
            {
                _shovel.Grab(Vec.From(point));
                _swallowedUps.Add(LeftUp);
                return true;
            }

            if (_snow.IsSnow(point))
            {
                _blockedClicks++;
                _swallowedUps.Add(message + 1);
                return true;
            }

            // Нажатие прошло к программе — его отпускание тоже должно пройти.
            _swallowedUps.Remove(message + 1);
            return false;
        }
    }
}

/// <summary>Пиксельная лопата, повёрнутая на свой угол.</summary>
public static class ShovelArt
{
    /// <summary>Картинка лопаты в точках арта (точка арта = <paramref name="scale"/> пикселей) и левый верхний угол на экране.</summary>
    public static (Picture Picture, ScreenPoint Origin) Render(Shovel shovel, int scale)
    {
        var reach = (shovel.Length / 2) + shovel.BladeWidth;
        var size = (int)Math.Ceiling(reach * 2 / scale) + 2;
        var picture = new Picture(size, size);
        var origin = new Vec(shovel.Position.X - (size * scale / 2.0), shovel.Position.Y - (size * scale / 2.0));
        var edge = scale * 1.0;
        var handleEnd = (shovel.Length / 2) - shovel.BladeLength;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var local = shovel.ToLocal(origin + new Vec((x + 0.5) * scale, (y + 0.5) * scale));
                picture[x, y] = Color(local, shovel, handleEnd, edge);
            }
        }

        return (picture, origin.ToPoint());
    }

    private static uint Color(Vec local, Shovel shovel, double handleEnd, double edge)
    {
        var halfBlade = shovel.BladeWidth / 2;
        var halfHandle = shovel.HandleWidth / 2;
        var tip = shovel.Length / 2;
        var back = -shovel.Length / 2;

        // Штык: металл с тёмным краем и светлой полосой у кромки.
        if (local.X >= handleEnd && local.X <= tip && Math.Abs(local.Y) <= halfBlade)
        {
            if (local.X > tip - edge || local.X < handleEnd + edge || Math.Abs(local.Y) > halfBlade - edge)
            {
                return Colors.Ink;
            }

            return local.X > tip - (edge * 3) ? Colors.White : Colors.Mist;
        }

        // Поперечная рукоятка на конце ручки.
        var gripLength = shovel.Length * 0.08;
        var gripHalf = halfHandle * 2.6;
        if (local.X >= back && local.X <= back + gripLength && Math.Abs(local.Y) <= gripHalf)
        {
            return local.X < back + edge || local.X > back + gripLength - edge || Math.Abs(local.Y) > gripHalf - edge
                ? Colors.Ink
                : Colors.RedDark;
        }

        // Деревянная ручка.
        if (local.X >= back && local.X <= handleEnd && Math.Abs(local.Y) <= halfHandle)
        {
            return Math.Abs(local.Y) > halfHandle - edge ? Colors.Ink : Colors.Amber;
        }

        return 0;
    }
}
