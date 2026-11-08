using ADHDWindows.Core.Display;
using ADHDWindows.Modules.Hardships.Screen;

namespace ADHDWindows.Modules.Hardships.Physics;

/// <summary>Настройки камнепада. Значения по умолчанию — базовые из идеи 014.</summary>
public sealed record RockSettings(
    double MinSize = 30,
    double MaxSize = 80,
    double MinPause = 1,
    double MaxPause = 3,
    double Gravity = 1100,
    double MaxFall = 1300);

/// <summary>Валун: падает с ускорением и пролетает сквозь окна. Потоками управляет владелец.</summary>
public sealed class Boulder(Vec position, double size, double drift)
{
    public Vec Position { get; private set; } = position;

    public Vec Velocity { get; private set; } = new(drift, 0);

    public double Size { get; } = size;

    public double Radius => Size / 2;

    /// <summary>Во сколько раз валун тяжелее самого маленького: крупный толкает сильнее.</summary>
    public double Weight(RockSettings settings) => Size / settings.MinSize;

    /// <summary>Поворот картинки: валун катится, пока падает.</summary>
    public double Turn { get; private set; }

    /// <summary>Окна, в заголовок которых этот валун уже попал.</summary>
    public HashSet<IntPtr> HitWindows { get; } = [];

    public void Step(double seconds, RockSettings settings)
    {
        var vy = Math.Min(settings.MaxFall, Velocity.Y + (settings.Gravity * seconds));
        Velocity = new Vec(Velocity.X, vy);
        Position += Velocity * seconds;
        Turn += (Velocity.X + (vy * 0.15)) * seconds / Math.Max(1, Radius);
    }

    /// <summary>
    /// Толкает точку курсора, если валун в неё попал: курсор выталкивается за край валуна, а скорость удара тем больше,
    /// чем валун тяжелее. Возвращает новую точку и скорость удара или null, если не попал.
    /// </summary>
    public (Vec Point, Vec Knock)? Push(Vec cursor, RockSettings settings)
    {
        var offset = cursor - Position;
        var distance = offset.Length;
        if (distance >= Radius)
        {
            return null;
        }

        // Точно в центр — выталкивает вниз.
        var normal = distance < 1e-6 ? new Vec(0, 1) : offset / distance;
        var point = Position + (normal * (Radius + 1));
        var impact = Math.Max(0, Vec.Dot(Velocity, normal));
        var knock = normal * (impact * Math.Min(1.6, Weight(settings) * 0.45));
        return (point, knock);
    }

    /// <summary>Нижняя точка валуна: ей он задевает заголовки окон.</summary>
    public Vec Bottom => Position + new Vec(0, Radius);
}

/// <summary>
/// Сдвиг курсора после удара валуном: скорость удара гаснет за доли секунды. Потоками управляет владелец.
/// </summary>
public sealed class Knock
{
    public const double Friction = 7;

    private Vec _velocity;
    private Vec _carry;

    public Vec Velocity => _velocity;

    public void Add(Vec velocity)
    {
        // Удары не складываются бесконечно: берётся сильнейший по каждой оси.
        _velocity = new Vec(
            Math.Abs(velocity.X) > Math.Abs(_velocity.X) ? velocity.X : _velocity.X,
            Math.Abs(velocity.Y) > Math.Abs(_velocity.Y) ? velocity.Y : _velocity.Y);
    }

    /// <summary>На сколько целых пикселей сдвинуть курсор за шаг.</summary>
    public ScreenPoint Step(double seconds)
    {
        _carry += _velocity * seconds;
        _velocity *= Math.Exp(-Friction * seconds);
        if (_velocity.Length < 5)
        {
            _velocity = Vec.Zero;
        }

        var step = new ScreenPoint((int)_carry.X, (int)_carry.Y);
        _carry -= new Vec(step.X, step.Y);
        return step;
    }

    public void Reset()
    {
        _velocity = Vec.Zero;
        _carry = Vec.Zero;
    }
}

/// <summary>Пиксельные валуны: неровный круг с тенью, бликом и тёмным контуром.</summary>
public static class RockArt
{
    public const int Scale = 3;

    /// <summary>Четыре кадра поворота валуна размером <paramref name="size"/> пикселей экрана.</summary>
    public static Picture[] Frames(double size, Random random)
    {
        var units = Math.Max(6, (int)Math.Round(size / Scale));
        var bumps = Enumerable.Range(0, 7).Select(_ => 0.82 + (random.NextDouble() * 0.18)).ToArray();
        var spots = Enumerable.Range(0, 3).Select(_ => (random.NextDouble() * 2 * Math.PI, 0.2 + (random.NextDouble() * 0.45))).ToArray();
        var first = new Picture(units, units);
        var center = (units - 1) / 2.0;
        for (var y = 0; y < units; y++)
        {
            for (var x = 0; x < units; x++)
            {
                var dx = (x - center) / (units / 2.0);
                var dy = (y - center) / (units / 2.0);
                var angle = Math.Atan2(dy, dx);
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                var edge = Edge(angle, bumps);
                if (distance > edge)
                {
                    continue;
                }

                uint color;
                if (distance > edge - (1.6 / units * 2))
                {
                    color = Colors.Ink;
                }
                else if (dx + dy > 0.55)
                {
                    color = Colors.Panel;
                }
                else if (dx + dy < -0.7)
                {
                    color = Colors.Mist;
                }
                else
                {
                    color = Colors.Line;
                }

                foreach (var (spotAngle, spotDistance) in spots)
                {
                    var sx = Math.Cos(spotAngle) * spotDistance;
                    var sy = Math.Sin(spotAngle) * spotDistance;
                    if (color != Colors.Ink && Math.Abs(dx - sx) < 0.12 && Math.Abs(dy - sy) < 0.12)
                    {
                        color = Colors.Panel;
                    }
                }

                first[x, y] = color;
            }
        }

        var frames = new Picture[4];
        frames[0] = first;
        for (var i = 1; i < 4; i++)
        {
            frames[i] = RotateQuarter(frames[i - 1]);
        }

        return frames;
    }

    private static double Edge(double angle, double[] bumps)
    {
        var position = (angle + Math.PI) / (2 * Math.PI) * bumps.Length;
        var index = (int)position % bumps.Length;
        var next = (index + 1) % bumps.Length;
        var t = position - Math.Floor(position);
        return (bumps[index] * (1 - t)) + (bumps[next] * t);
    }

    private static Picture RotateQuarter(Picture source)
    {
        var result = new Picture(source.Height, source.Width);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                result[source.Height - 1 - y, x] = source[x, y];
            }
        }

        return result;
    }
}
