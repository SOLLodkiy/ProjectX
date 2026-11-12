using System.Collections.Concurrent;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Logging;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Cosmetics;

/// <summary>Анимация эффекта: кадры и скорость.</summary>
public sealed record EffectArt(IReadOnlyList<Picture> Frames, int Fps)
{
    public const int DefaultFps = 12;

    public int FramesPerSecond => Fps > 0 ? Fps : DefaultFps;
}

/// <summary>
/// Эффекты косметики поверх экрана: след за курсором, вспышка на месте клика и фейерверк при покупке.
/// Каждая частица — маленькое окно, сквозь которое проходят клики. Окна живут в своём потоке и исчезают вместе
/// с программой, откатывать нечего. Пока эффектов нет, поток не работает.
/// </summary>
public sealed class EffectsPlayer : IDisposable
{
    /// <summary>Больше частиц одновременно не бывает: эффект не должен нагружать систему.</summary>
    public const int MaxParticles = 16;

    private const int TickMs = 25;
    private const int FireworkBursts = 6;
    private const double FireworkSpread = 0.9;

    private readonly ILog _log;
    private readonly Lock _lock = new();
    private readonly ConcurrentQueue<(EffectArt Art, ScreenPoint Center, double Delay)> _spawns = new();
    private readonly List<Particle> _particles = [];
    private readonly Stack<SpriteOverlay> _pool = new();
    private readonly Random _random = new();
    private OverlayThread? _overlay;
    private EffectArt? _trail;
    private EffectArt? _click;
    private EffectArt? _purchase;
    private volatile bool _paused;
    private ScreenPoint? _lastTrail;
    private int _errors;

    public EffectsPlayer(ILog log)
    {
        _log = log;
    }

    /// <summary>Увеличение картинок эффектов: под размер системного курсора.</summary>
    public static int Scale => Math.Max(2, CursorControl.SystemCursorSize() / 16);

    /// <summary>Эффекты временно не показываются: «Не беспокоить», полноэкранное окно или усложнение курсора.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value)
            {
                _spawns.Clear();
            }
        }
    }

    /// <summary>Поток оверлея запущен.</summary>
    public bool Running
    {
        get
        {
            lock (_lock)
            {
                return _overlay is not null;
            }
        }
    }

    /// <summary>Сколько частиц сейчас видно, для проверок.</summary>
    public int Visible
    {
        get
        {
            OverlayThread? overlay;
            lock (_lock)
            {
                overlay = _overlay;
            }

            return overlay?.Invoke(() => _particles.Count(particle => particle.Shown is not null)) ?? 0;
        }
    }

    /// <summary>Сколько окон оверлея создано, для проверок.</summary>
    public int Windows
    {
        get
        {
            OverlayThread? overlay;
            lock (_lock)
            {
                overlay = _overlay;
            }

            return overlay?.WindowCount ?? 0;
        }
    }

    /// <summary>Какие эффекты надеты. Все null — поток оверлея закрывается.</summary>
    public void Configure(EffectArt? trail, EffectArt? click, EffectArt? purchase)
    {
        OverlayThread? stop = null;
        lock (_lock)
        {
            _trail = trail;
            _click = click;
            _purchase = purchase;
            _lastTrail = null;
            if (trail is null && click is null && purchase is null)
            {
                stop = _overlay;
                _overlay = null;
            }
            else
            {
                _overlay ??= new OverlayThread("Cosmetics.Effects", Tick, OnError, TickMs);
            }
        }

        if (stop is not null)
        {
            _spawns.Clear();
            stop.Dispose();
            _particles.Clear();
            _pool.Clear();
        }
    }

    /// <summary>Вспышка на месте клика.</summary>
    public void Click(ScreenPoint point)
    {
        var art = _click;
        if (art is not null && !_paused)
        {
            _spawns.Enqueue((art, point, 0));
        }
    }

    /// <summary>Фейерверк в верхней половине <paramref name="area"/>: несколько вспышек одна за другой.</summary>
    public void Celebrate(ScreenRect area)
    {
        var art = _purchase;
        if (art is null || _paused)
        {
            return;
        }

        for (var i = 0; i < FireworkBursts; i++)
        {
            var x = area.Left + (area.Width / 2) + (int)((_random.NextDouble() - 0.5) * area.Width * FireworkSpread * 0.5);
            var y = area.Top + (area.Height / 4) + (int)((_random.NextDouble() - 0.5) * area.Height * 0.3);
            _spawns.Enqueue((art, new ScreenPoint(x, y), i * 0.18));
        }
    }

    public void Dispose() => Configure(null, null, null);

    private void Tick(double seconds)
    {
        while (_spawns.TryDequeue(out var spawn))
        {
            Spawn(spawn.Art, spawn.Center, spawn.Delay);
        }

        var trail = _trail;
        if (trail is not null && !_paused)
        {
            var position = CursorControl.Position();
            var spacing = 6 * Scale;
            if (_lastTrail is not { } last)
            {
                _lastTrail = position;
            }
            else if (Math.Abs(position.X - last.X) + Math.Abs(position.Y - last.Y) >= spacing)
            {
                _lastTrail = position;
                Spawn(trail, position, 0);
            }
        }

        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var particle = _particles[i];
            particle.Delay -= seconds;
            if (particle.Delay > 0)
            {
                continue;
            }

            particle.Age += seconds;
            var frame = (int)(particle.Age * particle.Art.FramesPerSecond);
            if (frame >= particle.Art.Frames.Count || _paused)
            {
                particle.Sprite.Hide();
                _pool.Push(particle.Sprite);
                _particles.RemoveAt(i);
                continue;
            }

            var picture = particle.Art.Frames[frame];
            if (!ReferenceEquals(picture, particle.Shown))
            {
                var scale = Scale;
                particle.Sprite.Show(
                    picture.Pixels,
                    picture.Width,
                    picture.Height,
                    scale,
                    new ScreenPoint(particle.Center.X - (picture.Width * scale / 2), particle.Center.Y - (picture.Height * scale / 2)));
                particle.Shown = picture;
            }
        }
    }

    private void Spawn(EffectArt art, ScreenPoint center, double delay)
    {
        if (_paused || art.Frames.Count == 0 || _particles.Count >= MaxParticles)
        {
            return;
        }

        var overlay = _overlay;
        if (overlay is null)
        {
            return;
        }

        var sprite = _pool.Count > 0 ? _pool.Pop() : overlay.CreateSprite(null);
        _particles.Add(new Particle(art, center, sprite) { Delay = delay });
    }

    private void OnError(Exception exception)
    {
        if (Interlocked.Increment(ref _errors) <= 3)
        {
            _log.Error("Ошибка эффектов косметики", exception);
        }
    }

    private sealed class Particle(EffectArt art, ScreenPoint center, SpriteOverlay sprite)
    {
        public EffectArt Art { get; } = art;

        public ScreenPoint Center { get; } = center;

        public SpriteOverlay Sprite { get; } = sprite;

        public double Delay { get; set; }

        public double Age { get; set; }

        public Picture? Shown { get; set; }
    }
}
