using ADHDWindows.Core.Display;

namespace ADHDWindows.Modules.Hardships.Screen;

/// <summary>Пиксельные фигуры из прямоугольников для оверлеев.</summary>
public static class PixelShapes
{
    /// <summary>
    /// Круг из полос высотой <paramref name="block"/>, края ступеньками по <paramref name="block"/> пикселей —
    /// так дыра в тумане и темноте выглядит пиксельной.
    /// </summary>
    public static List<ScreenRect> Circle(ScreenPoint center, int radius, int block)
    {
        block = Math.Max(1, block);
        var rects = new List<ScreenRect>();
        var rows = radius / block;
        for (var row = -rows; row < rows; row++)
        {
            // Середина полосы: так круг симметричен сверху и снизу.
            var middle = ((row * block) + (block / 2.0)) / radius;
            var half = (int)(Math.Sqrt(Math.Max(0, 1 - (middle * middle))) * radius / block) * block;
            if (half > 0)
            {
                rects.Add(new ScreenRect(center.X - half, center.Y + (row * block), half * 2, block));
            }
        }

        return rects;
    }
}

/// <summary>Падающие частицы для дождя и снега. Потоками управляет владелец.</summary>
public sealed class FallingField
{
    private readonly Random _random;
    private readonly FallSettings _settings;
    private readonly List<Particle> _particles = [];
    private ScreenRect _area;
    private double _time;

    public FallingField(Random random, FallSettings settings, ScreenRect area)
    {
        _random = random;
        _settings = settings;
        Resize(area);
    }

    public int Count => _particles.Count;

    /// <summary>Подстраивает число частиц под площадь экрана: плотность одинакова на любом мониторе.</summary>
    public void Resize(ScreenRect area)
    {
        if (area == _area)
        {
            return;
        }

        _area = area;
        var count = (int)Math.Clamp((long)area.Width * area.Height / _settings.AreaPerParticle, 20, _settings.MaxCount);
        while (_particles.Count > count)
        {
            _particles.RemoveAt(_particles.Count - 1);
        }

        while (_particles.Count < count)
        {
            _particles.Add(Spawn(anywhere: true));
        }
    }

    public void Step(double seconds, List<ScreenRect> output)
    {
        _time += seconds;
        for (var i = 0; i < _particles.Count; i++)
        {
            var particle = _particles[i];
            particle.Y += particle.Speed * seconds;
            particle.X += _settings.Slant * seconds;
            if (particle.Y > _area.Bottom || particle.X > _area.Right + 50 || particle.X < _area.Left - 50)
            {
                particle = Spawn(anywhere: false);
            }

            _particles[i] = particle;
            var sway = _settings.Sway > 0 ? Math.Sin((_time * 1.7) + particle.Phase) * _settings.Sway : 0;
            output.Add(new ScreenRect((int)(particle.X + sway), (int)particle.Y, _settings.Width, _settings.Height));
        }
    }

    private Particle Spawn(bool anywhere) => new()
    {
        X = _area.Left - (_settings.Slant * 0.5) + (_random.NextDouble() * _area.Width),
        Y = anywhere ? _area.Top + (_random.NextDouble() * _area.Height) : _area.Top - _settings.Height - (_random.NextDouble() * 60),
        Speed = _settings.MinSpeed + (_random.NextDouble() * (_settings.MaxSpeed - _settings.MinSpeed)),
        Phase = _random.NextDouble() * Math.PI * 2,
    };

    private struct Particle
    {
        public double X;
        public double Y;
        public double Speed;
        public double Phase;
    }
}

/// <summary>Параметры падающих частиц: размер в пикселях, скорость в пикселях в секунду, плотность.</summary>
public sealed record FallSettings(int Width, int Height, double MinSpeed, double MaxSpeed, double Slant, double Sway, long AreaPerParticle, int MaxCount)
{
    public static readonly FallSettings Rain = new(3, 14, 700, 950, 90, 0, 9_000, 700);

    public static readonly FallSettings Snow = new(6, 6, 50, 110, 10, 18, 22_000, 300);
}

/// <summary>
/// Монетная пыль: за движущимся курсором сыплются золотые частицы, которые падают и уменьшаются.
/// Потоками управляет владелец.
/// </summary>
public sealed class CoinDust(Random random)
{
    public const double Life = 0.9;
    public const double SpawnDistance = 10;
    public const int MaxCount = 160;
    public const int Size = 6;

    private readonly List<Grain> _grains = [];
    private ScreenPoint? _last;
    private double _travel;

    public int Count => _grains.Count;

    public void Step(double seconds, ScreenPoint cursor, List<ScreenRect> output)
    {
        if (_last is { } last)
        {
            var dx = cursor.X - last.X;
            var dy = cursor.Y - last.Y;
            _travel += Math.Sqrt((dx * dx) + (dy * dy));
            while (_travel >= SpawnDistance && _grains.Count < MaxCount)
            {
                _travel -= SpawnDistance;
                _grains.Add(new Grain
                {
                    // Частицы появляются чуть позади и сбоку от острия стрелки.
                    X = cursor.X + 4 + random.Next(-6, 7),
                    Y = cursor.Y + 10 + random.Next(-4, 9),
                    Vx = (random.NextDouble() - 0.5) * 120,
                    Vy = -40 - (random.NextDouble() * 80),
                    Age = 0,
                });
            }

            _travel = Math.Min(_travel, SpawnDistance);
        }

        _last = cursor;
        for (var i = _grains.Count - 1; i >= 0; i--)
        {
            var grain = _grains[i];
            grain.Age += seconds;
            if (grain.Age >= Life)
            {
                _grains.RemoveAt(i);
                continue;
            }

            grain.Vy += 600 * seconds;
            grain.X += grain.Vx * seconds;
            grain.Y += grain.Vy * seconds;
            _grains[i] = grain;
            var size = Math.Max(2, (int)Math.Round(Size * (1 - (grain.Age / Life))));
            output.Add(new ScreenRect((int)grain.X, (int)grain.Y, size, size));
        }
    }

    private struct Grain
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Age;
    }
}
