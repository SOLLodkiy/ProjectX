using ADHDWindows.Core.Display;

namespace ADHDWindows.Modules.Hardships.Physics;

/// <summary>Настройки курсора на цепи. Значения по умолчанию — базовые из идеи 014.</summary>
public sealed record ChainSettings(
    double Length = 200,
    double MassRatio = 2,
    double WanderSpeed = 90,
    double DashSpeed = 1100,
    double MinDashPause = 4,
    double MaxDashPause = 8);

/// <summary>
/// Второй курсор на цепи, которым управляет бот. Цепь натягивается только на полной длине. Бот тяжелее курсора
/// человека, поэтому при натянутой цепи больше сдвигается курсор человека; резкий рывок человека передаёт боту
/// скорость, и тогда перетягивание выигрывает человек. Потоками управляет владелец.
/// </summary>
public sealed class ChainBot
{
    /// <summary>С какой скорости рывок человека начинает побеждать бота, пикселей в секунду.</summary>
    public const double JerkStart = 500;

    /// <summary>С какой скорости рывок человека побеждает полностью.</summary>
    public const double JerkFull = 1800;

    public const double DashFriction = 2.5;

    private readonly Random _random;
    private readonly ChainSettings _settings;
    private double _heading;
    private double _dashIn;
    private bool _dashing;

    public ChainBot(Random random, ChainSettings settings, Vec start)
    {
        _random = random;
        _settings = settings;
        Position = start;
        _heading = random.NextDouble() * Math.PI * 2;
        _dashIn = NextPause();
    }

    public Vec Position { get; private set; }

    public Vec Velocity { get; private set; }

    public double Length => _settings.Length;

    /// <summary>Сколько было рывков.</summary>
    public int Dashes { get; private set; }

    /// <summary>
    /// Шаг. <paramref name="user"/> — курсор человека после его собственного движения, <paramref name="userVelocity"/> —
    /// скорость его руки. Пока <paramref name="held"/> (нажата кнопка), бот не тянет человека, а тащится сам.
    /// Возвращает, на сколько сдвинуть курсор человека.
    /// </summary>
    public Vec Step(double seconds, Vec user, Vec userVelocity, bool held, ScreenRect bounds)
    {
        if (seconds <= 0)
        {
            return Vec.Zero;
        }

        _dashIn -= seconds;
        if (_dashIn <= 0)
        {
            _dashIn = NextPause();
            Dash(user);
        }

        if (_dashing)
        {
            Velocity *= Math.Exp(-DashFriction * seconds);
            _dashing = Velocity.Length > _settings.WanderSpeed * 1.5;
        }
        else
        {
            // Неспешно бродит, понемногу поворачивая.
            _heading += (_random.NextDouble() - 0.5) * 3 * seconds * 2;
            var desired = Vec.FromAngle(_heading) * _settings.WanderSpeed;
            Velocity += (desired - Velocity) * Math.Min(1, seconds * 2);
        }

        Position += Velocity * seconds;
        Bounce(bounds);

        var shift = Vec.Zero;
        var offset = Position - user;
        var distance = offset.Length;
        if (distance > _settings.Length)
        {
            var away = offset / distance;
            var excess = distance - _settings.Length;
            var botShare = 1 / (1 + _settings.MassRatio);

            // Резкий рывок человека от бота: бот получает скорость и летит за ним.
            var pull = Vec.Dot(userVelocity, -away);
            var jerk = Math.Clamp((pull - JerkStart) / (JerkFull - JerkStart), 0, 1);
            if (held)
            {
                botShare = 1;
            }
            else
            {
                botShare += (1 - botShare) * jerk;
            }

            Position -= away * (excess * botShare);
            shift = away * (excess * (1 - botShare));
            if (jerk > 0)
            {
                Velocity += -away * (pull * jerk * 0.6);
                _dashing = true;
            }

            // Цепь гасит скорость бота от человека.
            var outward = Vec.Dot(Velocity, away);
            if (outward > 0)
            {
                Velocity -= away * (outward * 0.7);
            }
        }

        return shift;
    }

    private void Dash(Vec user)
    {
        // Чаще рвётся прочь от человека: так цепь натягивается и тащит курсор.
        var away = (Position - user).Normalized;
        var angle = away == Vec.Zero || _random.NextDouble() < 0.35
            ? _random.NextDouble() * Math.PI * 2
            : Math.Atan2(away.Y, away.X) + ((_random.NextDouble() - 0.5) * 1.2);
        Velocity = Vec.FromAngle(angle) * _settings.DashSpeed;
        _heading = angle;
        _dashing = true;
        Dashes++;
    }

    private double NextPause() => _settings.MinDashPause + (_random.NextDouble() * (_settings.MaxDashPause - _settings.MinDashPause));

    private void Bounce(ScreenRect bounds)
    {
        var x = Position.X;
        var y = Position.Y;
        var vx = Velocity.X;
        var vy = Velocity.Y;
        if (x < bounds.Left || x > bounds.Right - 1)
        {
            vx = -vx * 0.5;
            x = Math.Clamp(x, bounds.Left, bounds.Right - 1);
            _heading = Math.PI - _heading;
        }

        if (y < bounds.Top || y > bounds.Bottom - 1)
        {
            vy = -vy * 0.5;
            y = Math.Clamp(y, bounds.Top, bounds.Bottom - 1);
            _heading = -_heading;
        }

        Position = new Vec(x, y);
        Velocity = new Vec(vx, vy);
    }
}

/// <summary>Звенья цепи между двумя точками: натянутая цепь прямая, свободная провисает вниз.</summary>
public static class ChainLinks
{
    /// <summary>Середины звеньев с шагом <paramref name="spacing"/> вдоль цепи длины <paramref name="length"/>.</summary>
    public static List<Vec> Points(Vec from, Vec to, double length, double spacing)
    {
        var distance = (to - from).Length;
        var sag = Math.Sqrt(Math.Max(0, (length * length) - (distance * distance))) / 2;
        var control = ((from + to) / 2) + new Vec(0, sag);
        var count = Math.Max(2, (int)(length / spacing));
        var points = new List<Vec>(count + 1);
        for (var i = 0; i <= count; i++)
        {
            var t = i / (double)count;
            var a = from * ((1 - t) * (1 - t));
            var b = control * (2 * (1 - t) * t);
            var c = to * (t * t);
            points.Add(a + b + c);
        }

        return points;
    }
}
