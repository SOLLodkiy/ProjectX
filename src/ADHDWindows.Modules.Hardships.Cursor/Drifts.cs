using ADHDWindows.Core.Display;

namespace ADHDWindows.Modules.Hardships.Cursor;

/// <summary>
/// Добавка к движению курсора на один шаг цикла. <paramref name="userDx"/>, <paramref name="userDy"/> — сколько
/// сдвинул курсор сам человек с прошлого шага. Ответ — на сколько пикселей сдвинуть курсор сверху.
/// Потоками управляет владелец.
/// </summary>
public interface IDrift
{
    (double Dx, double Dy) Step(double seconds, double userDx, double userDy, ScreenPoint position);

    /// <summary>Человек держит кнопку мыши: добавка не действует и начинается заново после отпускания.</summary>
    void Reset();
}

/// <summary>Ветер: курсор сносит в сторону, которая медленно и случайно поворачивается.</summary>
public sealed class WindDrift(Random random, double speed) : IDrift
{
    /// <summary>Как быстро поворачивается ветер, радиан в секунду в среднем.</summary>
    public const double TurnRate = 0.6;

    private double _angle = random.NextDouble() * Math.PI * 2;

    public double Angle => _angle;

    public (double Dx, double Dy) Step(double seconds, double userDx, double userDy, ScreenPoint position)
    {
        _angle += (random.NextDouble() - 0.5) * 2 * TurnRate * Math.Sqrt(seconds) * 3;
        return (Math.Cos(_angle) * speed * seconds, Math.Sin(_angle) * speed * seconds);
    }

    public void Reset()
    {
    }
}

/// <summary>
/// Лёд: курсор скользит. Скорость человека подхватывается и после остановки гаснет постепенно,
/// поэтому курсор проезжает дальше, чем его вели.
/// </summary>
public sealed class IceSlide(double slide = IceSlide.DefaultSlide, double friction = IceSlide.DefaultFriction) : IDrift
{
    public const double DefaultSlide = 0.7;
    public const double DefaultFriction = 2.2;

    private double _vx;
    private double _vy;

    public double Speed => Math.Sqrt((_vx * _vx) + (_vy * _vy));

    public (double Dx, double Dy) Step(double seconds, double userDx, double userDy, ScreenPoint position)
    {
        if (seconds <= 0)
        {
            return (0, 0);
        }

        if (userDx != 0 || userDy != 0)
        {
            // Скорость набирается за доли секунды, как будто курсор разгоняют по льду.
            var pick = Math.Min(1, seconds * 6);
            _vx += ((userDx / seconds) - _vx) * pick;
            _vy += ((userDy / seconds) - _vy) * pick;
        }
        else
        {
            var decay = Math.Exp(-friction * seconds);
            _vx *= decay;
            _vy *= decay;
        }

        return (_vx * slide * seconds, _vy * slide * seconds);
    }

    public void Reset()
    {
        _vx = 0;
        _vy = 0;
    }
}

/// <summary>Дрожь: курсор подрагивает на несколько пикселей вокруг места, куда его ведут.</summary>
public sealed class Tremor(Random random, int amplitude = Tremor.DefaultAmplitude) : IDrift
{
    public const int DefaultAmplitude = 3;
    public const double ShakeInterval = 0.05;

    private double _sinceShake;
    private int _offsetX;
    private int _offsetY;

    public (double Dx, double Dy) Step(double seconds, double userDx, double userDy, ScreenPoint position)
    {
        _sinceShake += seconds;
        if (_sinceShake < ShakeInterval)
        {
            return (0, 0);
        }

        _sinceShake = 0;
        var x = random.Next(-amplitude, amplitude + 1);
        var y = random.Next(-amplitude, amplitude + 1);
        var result = (x - _offsetX, y - _offsetY);
        _offsetX = x;
        _offsetY = y;
        return result;
    }

    public void Reset()
    {
        _offsetX = 0;
        _offsetY = 0;
    }
}

/// <summary>Пьяный курсор: плавно качается по кругу вокруг места, куда его ведут.</summary>
public sealed class Wobble(Random random, double radius = Wobble.DefaultRadius) : IDrift
{
    public const double DefaultRadius = 22;

    private readonly double _speedX = 1.1 + random.NextDouble();
    private readonly double _speedY = 0.8 + random.NextDouble();
    private double _time;
    private double _offsetX;
    private double _offsetY;

    public (double Dx, double Dy) Step(double seconds, double userDx, double userDy, ScreenPoint position)
    {
        _time += seconds;
        var x = Math.Sin(_time * _speedX) * radius;
        var y = Math.Sin((_time * _speedY) + 1) * radius * 0.7;
        var result = (x - _offsetX, y - _offsetY);
        _offsetX = x;
        _offsetY = y;
        return result;
    }

    public void Reset()
    {
        _offsetX = 0;
        _offsetY = 0;
    }
}

/// <summary>Телепорт: раз в несколько секунд курсор прыгает на небольшое расстояние в случайную сторону.</summary>
public sealed class Teleport(Random random) : IDrift
{
    public const double MinPause = 1.5;
    public const double MaxPause = 4;
    public const int MinJump = 80;
    public const int MaxJump = 200;

    private double _wait = Pause(random);

    public (double Dx, double Dy) Step(double seconds, double userDx, double userDy, ScreenPoint position)
    {
        _wait -= seconds;
        if (_wait > 0)
        {
            return (0, 0);
        }

        _wait = Pause(random);
        var angle = random.NextDouble() * Math.PI * 2;
        var distance = random.Next(MinJump, MaxJump + 1);
        return (Math.Round(Math.Cos(angle) * distance), Math.Round(Math.Sin(angle) * distance));
    }

    public void Reset() => _wait = Math.Max(_wait, 0.5);

    private static double Pause(Random random) => MinPause + (random.NextDouble() * (MaxPause - MinPause));
}

/// <summary>Магнит: курсор тянет к точке, которая медленно бродит по экрану. Чем дальше точка, тем слабее тяга.</summary>
public sealed class Magnet(Random random, Func<ScreenPoint, ScreenRect> screenOf, double pull = Magnet.DefaultPull) : IDrift
{
    public const double DefaultPull = 70;
    public const double MoveEvery = 6;

    private ScreenPoint? _target;
    private double _sinceMove = MoveEvery;

    public ScreenPoint? Target => _target;

    public (double Dx, double Dy) Step(double seconds, double userDx, double userDy, ScreenPoint position)
    {
        _sinceMove += seconds;
        if (_target is null || _sinceMove >= MoveEvery)
        {
            _sinceMove = 0;
            var screen = screenOf(position);
            _target = new ScreenPoint(
                screen.Left + (screen.Width / 6) + random.Next(Math.Max(1, screen.Width * 2 / 3)),
                screen.Top + (screen.Height / 6) + random.Next(Math.Max(1, screen.Height * 2 / 3)));
        }

        var dx = _target.Value.X - position.X;
        var dy = _target.Value.Y - position.Y;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance < 2)
        {
            return (0, 0);
        }

        // У самой точки тяга сильная, на другом конце экрана — вдвое слабее.
        var strength = pull * (1 / (1 + (distance / 1500))) * seconds;
        var step = Math.Min(strength, distance);
        return (dx / distance * step, dy / distance * step);
    }

    public void Reset()
    {
    }
}
