using System.Diagnostics;
using System.Globalization;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Cursor;

/// <summary>
/// Усложнение, которое сдвигает курсор поверх движения человека. Пока нажата кнопка мыши, курсор не трогается:
/// иначе сбилось бы выделение или перетаскивание файла.
/// </summary>
public abstract class DriftHardship : HardshipModule
{
    private readonly Lock _lock = new();
    private IDrift[] _drifts = [];
    private Ticker? _ticker;
    private double _carryX;
    private double _carryY;
    private ScreenPoint? _expected;

    /// <summary>Добавки, которые действуют вместе.</summary>
    protected abstract IEnumerable<IDrift> CreateDrifts(Random random);

    protected override void Begin()
    {
        lock (_lock)
        {
            _drifts = [.. CreateDrifts(new Random())];
            _carryX = 0;
            _carryY = 0;
            _expected = null;
        }

        _ticker = new Ticker(Info.Id, Step, error => Context.Log.Error($"Ошибка усложнения {Info.Id}", error));
    }

    protected override void End()
    {
        _ticker?.Dispose();
        _ticker = null;
    }

    private void Step(double seconds)
    {
        lock (_lock)
        {
            var position = CursorControl.Position();
            if (CursorControl.AnyButtonDown())
            {
                foreach (var drift in _drifts)
                {
                    drift.Reset();
                }

                _carryX = 0;
                _carryY = 0;
                _expected = position;
                return;
            }

            double userX = 0;
            double userY = 0;
            if (_expected is { } expected)
            {
                userX = position.X - expected.X;
                userY = position.Y - expected.Y;
            }

            foreach (var drift in _drifts)
            {
                var (dx, dy) = drift.Step(seconds, userX, userY, position);
                _carryX += dx;
                _carryY += dy;
            }

            var stepX = (int)_carryX;
            var stepY = (int)_carryY;
            _carryX -= stepX;
            _carryY -= stepY;
            if (stepX != 0 || stepY != 0)
            {
                CursorControl.MoveTo(new ScreenPoint(position.X + stepX, position.Y + stepY));
                position = CursorControl.Position();
            }

            _expected = position;
        }
    }

    /// <summary>Монитор с точкой, для добавок, которым нужны границы экрана.</summary>
    protected static ScreenRect ScreenOf(ScreenPoint point) => ScreenBounds.Of(Desktop.Screens(), point);
}

/// <summary>Ветер: курсор слегка сносит в случайную сторону.</summary>
public sealed class WindHardship : DriftHardship
{
    public const double Speed = 30;

    public override HardshipInfo Info { get; } = new("wind", HardshipLevel.Easy, HardshipResources.CursorMotion);

    protected override IEnumerable<IDrift> CreateDrifts(Random random) => [new WindDrift(random, Speed)];
}

/// <summary>Лёд: курсор скользит и продолжает движение по инерции.</summary>
public sealed class IceHardship : DriftHardship
{
    public override HardshipInfo Info { get; } = new("ice", HardshipLevel.Medium, HardshipResources.CursorMotion);

    protected override IEnumerable<IDrift> CreateDrifts(Random random) => [new IceSlide()];
}

/// <summary>Шторм: сильный ветер и лёд одновременно.</summary>
public sealed class StormHardship : DriftHardship
{
    public const double Speed = 55;

    public override HardshipInfo Info { get; } = new("storm", HardshipLevel.Hard, HardshipResources.CursorMotion);

    protected override IEnumerable<IDrift> CreateDrifts(Random random) => [new WindDrift(random, Speed), new IceSlide(slide: 0.9, friction: 1.6)];
}

/// <summary>Дрожь: курсор немного дрожит.</summary>
public sealed class TremorHardship : DriftHardship
{
    public override HardshipInfo Info { get; } = new("tremor", HardshipLevel.Medium, HardshipResources.CursorMotion);

    protected override IEnumerable<IDrift> CreateDrifts(Random random) => [new Tremor(random)];
}

/// <summary>Пьяный курсор: плавно качается из стороны в сторону.</summary>
public sealed class DrunkHardship : DriftHardship
{
    public override HardshipInfo Info { get; } = new("drunk", HardshipLevel.Medium, HardshipResources.CursorMotion);

    protected override IEnumerable<IDrift> CreateDrifts(Random random) => [new Wobble(random)];
}

/// <summary>Телепорт: курсор иногда прыгает на небольшое расстояние.</summary>
public sealed class TeleportHardship : DriftHardship
{
    public override HardshipInfo Info { get; } = new("teleport", HardshipLevel.Hard, HardshipResources.CursorMotion);

    protected override IEnumerable<IDrift> CreateDrifts(Random random) => [new Teleport(random)];
}

/// <summary>Магнит: курсор тянет к бродячей точке на экране.</summary>
public sealed class MagnetHardship : DriftHardship
{
    public override HardshipInfo Info { get; } = new("magnet", HardshipLevel.Medium, HardshipResources.CursorMotion);

    protected override IEnumerable<IDrift> CreateDrifts(Random random) => [new Magnet(random, ScreenOf)];
}

/// <summary>Хрупкий курсор: при слишком быстром движении возвращается туда, где был полсекунды назад.</summary>
public sealed class GlassHardship : HardshipModule
{
    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = new();
    private GlassGuard _guard = new();
    private Ticker? _ticker;

    public override HardshipInfo Info { get; } = new("glass", HardshipLevel.Medium, HardshipResources.CursorMotion);

    protected override void Begin()
    {
        lock (_lock)
        {
            _guard = new GlassGuard();
            _clock.Restart();
        }

        _ticker = new Ticker("Glass", _ => Step(), error => Context.Log.Error("Ошибка хрупкого курсора", error));
    }

    protected override void End()
    {
        _ticker?.Dispose();
        _ticker = null;
        lock (_lock)
        {
            Context.Log.Info($"Хрупкий курсор разбился {_guard.Breaks} раз");
        }
    }

    private void Step()
    {
        lock (_lock)
        {
            if (CursorControl.AnyButtonDown())
            {
                _guard.Reset();
                return;
            }

            if (_guard.Step(_clock.Elapsed.TotalSeconds, CursorControl.Position()) is { } back)
            {
                CursorControl.MoveTo(back);
            }
        }
    }
}

/// <summary>
/// Усложнение, которое перехватывает движение мыши до системы и решает, куда поставить курсор.
/// Работает и при нажатой кнопке: движение остаётся управляемым, просто по другим правилам.
/// </summary>
public abstract class FilterHardship : HardshipModule
{
    private MouseMoveFilter? _filter;

    protected override void Begin()
    {
        OnBegin();
        _filter = new MouseMoveFilter(Filter, Context.Log);
        if (!_filter.Start())
        {
            throw new InvalidOperationException("Фильтр движения мыши не поставлен");
        }
    }

    protected override void End()
    {
        _filter?.Dispose();
        _filter = null;
        OnEnd();
    }

    /// <summary>Вызывается в потоке хука при каждом движении: null — пропустить, точка — поставить курсор туда.</summary>
    protected abstract ScreenPoint? Filter(ScreenPoint current, ScreenPoint target);

    /// <summary>Решение фильтра без хука, для проверок: события от программ хук пропускает, поэтому их так не проверить.</summary>
    internal ScreenPoint? Apply(ScreenPoint current, ScreenPoint target) => Filter(current, target);

    protected virtual void OnBegin()
    {
    }

    protected virtual void OnEnd()
    {
    }
}

/// <summary>Инверсия: движение курсора инвертировано по обеим осям.</summary>
public sealed class InversionHardship : FilterHardship
{
    public override HardshipInfo Info { get; } = new("inversion", HardshipLevel.Hard, HardshipResources.CursorMotion);

    protected override ScreenPoint? Filter(ScreenPoint current, ScreenPoint target) =>
        new ScreenPoint(current.X - (target.X - current.X), current.Y - (target.Y - current.Y));
}

/// <summary>Зеркало: инвертировано только движение влево-вправо.</summary>
public sealed class MirrorHardship : FilterHardship
{
    public override HardshipInfo Info { get; } = new("mirror", HardshipLevel.Medium, HardshipResources.CursorMotion);

    protected override ScreenPoint? Filter(ScreenPoint current, ScreenPoint target) =>
        new ScreenPoint(current.X - (target.X - current.X), target.Y);
}

/// <summary>Задержка: курсор следует за мышью с небольшим отставанием.</summary>
public sealed class LagHardship : FilterHardship
{
    private static readonly TimeSpan ScreensRefresh = TimeSpan.FromSeconds(2);

    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = new();
    private LagLine _line = new();
    private IReadOnlyList<ScreenArea> _screens = [];
    private TimeSpan _screensRead;
    private ScreenPoint _virtual;
    private Ticker? _ticker;

    public override HardshipInfo Info { get; } = new("lag", HardshipLevel.Medium, HardshipResources.CursorMotion);

    protected override void OnBegin()
    {
        lock (_lock)
        {
            _clock.Restart();
            _screens = Desktop.Screens();
            _screensRead = TimeSpan.Zero;
            _virtual = CursorControl.Position();
            _line = new LagLine();
            _line.Start(_virtual);
        }

        _ticker = new Ticker("Lag", _ => Show(), error => Context.Log.Error("Ошибка задержки курсора", error));
    }

    protected override void OnEnd()
    {
        _ticker?.Dispose();
        _ticker = null;

        // Курсор остаётся там, куда его довели, а не там, где его показывала задержка.
        lock (_lock)
        {
            CursorControl.MoveTo(_virtual);
        }
    }

    protected override ScreenPoint? Filter(ScreenPoint current, ScreenPoint target)
    {
        lock (_lock)
        {
            // Смещение считается от показанного курсора: его система и сдвинула бы.
            _virtual = ScreenBounds.Clamp(_screens, new ScreenPoint(_virtual.X + (target.X - current.X), _virtual.Y + (target.Y - current.Y)));
            _line.Add(_clock.Elapsed.TotalSeconds, _virtual);
            return current;
        }
    }

    private void Show()
    {
        ScreenPoint shown;
        lock (_lock)
        {
            if (_clock.Elapsed - _screensRead > ScreensRefresh)
            {
                _screensRead = _clock.Elapsed;
                _screens = Desktop.Screens();
            }

            shown = _line.At(_clock.Elapsed.TotalSeconds);
        }

        if (shown != CursorControl.Position())
        {
            CursorControl.MoveTo(shown);
        }
    }
}

/// <summary>Улитка: курсор становится заметно медленнее.</summary>
public sealed class SnailHardship : HardshipModule
{
    /// <summary>Во сколько раз падает скорость указателя.</summary>
    public const double Slowdown = 0.4;

    private IDisposable? _mark;
    private int _original;

    public override HardshipInfo Info { get; } = new("snail", HardshipLevel.Easy, HardshipResources.CursorMotion);

    protected override void Begin()
    {
        _original = CursorControl.Speed();
        _mark = Context.Changes.Record(MouseSpeedRestorer.ChangeKind, _original.ToString(CultureInfo.InvariantCulture));
        CursorControl.SetSpeed(Math.Max(CursorControl.MinSpeed, (int)Math.Round(_original * Slowdown)));
    }

    protected override void End()
    {
        CursorControl.SetSpeed(_original);
        _mark?.Dispose();
        _mark = null;
    }
}
