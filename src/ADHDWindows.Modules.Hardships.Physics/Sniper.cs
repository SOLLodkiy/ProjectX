namespace ADHDWindows.Modules.Hardships.Physics;

/// <summary>Настройки снайпера. Значения по умолчанию — базовые из идеи 014.</summary>
public sealed record SniperSettings(
    double Delay = 0.9,
    double Aim = 2,
    double Freeze = 5,
    double Hidden = 7,
    double LockRadius = 10);

/// <summary>Что делает снайпер сейчас.</summary>
public enum SniperState
{
    /// <summary>Прицел идёт по следу курсора.</summary>
    Tracking,

    /// <summary>Прицел на курсоре, идёт отсчёт до выстрела.</summary>
    Aiming,

    /// <summary>Выстрел был: курсор обездвижен, прицела не видно.</summary>
    Reloading,
}

/// <summary>
/// Снайпер: прицел в точности повторяет путь курсора с запаздыванием — где курсор задержался, там задерживается и
/// прицел, быстрее курсора он не движется. Когда прицел на курсоре, идёт отсчёт; курсор ушёл — отсчёт сначала.
/// После выстрела курсор обездвижен, а прицел пропадает и потом снова идёт по следу. Потоками управляет владелец.
/// </summary>
public sealed class SniperSight(SniperSettings settings)
{
    private readonly Queue<(double Time, Vec Point)> _trail = new();
    private Vec _sight;
    private double _aimStarted;
    private double _shotAt = double.NegativeInfinity;
    private bool _started;

    public SniperSettings Settings => settings;

    public SniperState State { get; private set; }

    /// <summary>Где прицел. Пока снайпер перезаряжается, прицела не видно.</summary>
    public Vec Sight => _sight;

    public bool SightVisible => State != SniperState.Reloading;

    /// <summary>Доля отсчёта до выстрела от 0 до 1.</summary>
    public double AimProgress { get; private set; }

    public int Shots { get; private set; }

    /// <summary>Обездвижен ли курсор в момент <paramref name="now"/>.</summary>
    public bool IsFrozen(double now) => now - _shotAt < settings.Freeze;

    /// <summary>Шаг. Возвращает true в момент выстрела.</summary>
    public bool Step(double now, Vec cursor)
    {
        if (!_started)
        {
            _started = true;
            _sight = cursor;
        }

        _trail.Enqueue((now, cursor));
        while (_trail.Count > 0 && now - _trail.Peek().Time >= settings.Delay)
        {
            _sight = _trail.Dequeue().Point;
        }

        if (State == SniperState.Reloading)
        {
            AimProgress = 0;
            if (now - _shotAt >= settings.Hidden)
            {
                State = SniperState.Tracking;
            }

            return false;
        }

        if ((_sight - cursor).Length > settings.LockRadius)
        {
            State = SniperState.Tracking;
            AimProgress = 0;
            return false;
        }

        if (State != SniperState.Aiming)
        {
            State = SniperState.Aiming;
            _aimStarted = now;
        }

        AimProgress = Math.Clamp((now - _aimStarted) / settings.Aim, 0, 1);
        if (now - _aimStarted < settings.Aim)
        {
            return false;
        }

        State = SniperState.Reloading;
        _shotAt = now;
        AimProgress = 0;
        Shots++;
        return true;
    }
}
