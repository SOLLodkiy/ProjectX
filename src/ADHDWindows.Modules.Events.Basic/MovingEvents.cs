using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.RandomEvents;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Events.Basic;

/// <summary>
/// Настройки монетного дождя: сколько секунд идёт, сколько монет в секунду, награда за монету, радиус ловли
/// курсором и скорость падения в пикселях в секунду.
/// </summary>
public sealed record CoinRainSettings(
    double Seconds = 20,
    double PerSecond = 3,
    long Reward = 5,
    int CatchRadius = 48,
    double MinSpeed = 160,
    double MaxSpeed = 320,
    int Scale = 3);

/// <summary>
/// Монетный дождь: монетки падают с верха экрана, их ловят, наводя на них курсор. Монетки не перехватывают клики,
/// поэтому работе под ними дождь не мешает.
/// </summary>
public sealed class CoinRainEvent : OverlayEvent
{
    private const int PoolSize = 24;

    private readonly List<Drop> _drops = [];
    private CoinRainSettings _settings = new();
    private ScreenRect _area;
    private double _age;
    private double _spawn;
    private long _earned;
    private int _caught;
    private bool _done;

    public override RandomEventInfo Info { get; } = new("coinRain", HardshipResources.Screen);

    /// <summary>Сколько монеток поймано, для проверок.</summary>
    internal int Caught => Overlay.Invoke(() => _caught);

    /// <summary>Где сейчас монетки, для проверок.</summary>
    internal IReadOnlyList<ScreenRect> Coins => Overlay.Invoke(() => _drops.Where(drop => drop.Falling).Select(drop => drop.Sprite.Bounds).ToList());

    /// <summary>Курсор, для проверок подменяется.</summary>
    internal Func<ScreenPoint> Cursor { get; set; } = CursorControl.Position;

    protected override void OnStart() =>
        _settings = EventData.Load(Context.Data, "events/rewards", "coinRain", new CoinRainSettings(), Context.Log);

    protected override void Create()
    {
        _drops.Clear();
        _area = EventArea.WorkArea();
        _age = 0;
        _spawn = 0;
        _earned = 0;
        _caught = 0;
        _done = false;
        for (var index = 0; index < PoolSize; index++)
        {
            _drops.Add(new Drop(Overlay.CreateSprite(null)));
        }
    }

    protected override void Tick(double seconds)
    {
        if (_done)
        {
            return;
        }

        _age += seconds;
        if (_age < _settings.Seconds)
        {
            _spawn += seconds * _settings.PerSecond;
            while (_spawn >= 1)
            {
                _spawn -= 1;
                Spawn();
            }
        }

        var cursor = Cursor();
        var size = EventPictures.SmallCoin.Width * _settings.Scale;
        foreach (var drop in _drops.Where(drop => drop.Falling))
        {
            drop.Y += drop.Speed * seconds;
            var center = new ScreenPoint((int)drop.X + (size / 2), (int)drop.Y + (size / 2));
            var dx = center.X - cursor.X;
            var dy = center.Y - cursor.Y;
            if ((dx * dx) + (dy * dy) <= _settings.CatchRadius * _settings.CatchRadius)
            {
                drop.Falling = false;
                drop.Sprite.Hide();
                _caught++;
                _earned += Context.Economy.Earn(EventSources.Event, _settings.Reward);
            }
            else if (drop.Y > _area.Bottom)
            {
                drop.Falling = false;
                drop.Sprite.Hide();
            }
            else
            {
                drop.Sprite.MoveTo(new ScreenPoint((int)drop.X, (int)drop.Y));
            }
        }

        if (_age >= _settings.Seconds && _drops.All(drop => !drop.Falling))
        {
            _done = true;
            Context.Log.Info($"Монетный дождь: поймано {_caught}");
            Complete(_caught > 0, _earned);
        }
    }

    protected override void Finish() => _drops.Clear();

    private void Spawn()
    {
        if (_drops.FirstOrDefault(drop => !drop.Falling) is not { } drop)
        {
            return;
        }

        var size = EventPictures.SmallCoin.Width * _settings.Scale;
        drop.X = _area.Left + Random.Next(Math.Max(1, _area.Width - size));
        drop.Y = _area.Top - size;
        drop.Speed = _settings.MinSpeed + (Random.NextDouble() * Math.Max(0, _settings.MaxSpeed - _settings.MinSpeed));
        drop.Falling = true;
        var picture = EventPictures.SmallCoin;
        drop.Sprite.Show(picture.Pixels, picture.Width, picture.Height, _settings.Scale, new ScreenPoint((int)drop.X, (int)drop.Y));
    }

    private sealed class Drop(SpriteOverlay sprite)
    {
        public SpriteOverlay Sprite { get; } = sprite;

        public double X { get; set; }

        public double Y { get; set; }

        public double Speed { get; set; }

        public bool Falling { get; set; }
    }
}

/// <summary>Настройки гоблина: скорость бега и награда за клик.</summary>
public sealed record GoblinSettings(double Speed = 380, long MinReward = 60, long MaxReward = 140, int Scale = 4);

/// <summary>Бегущий гоблин: пробегает по экрану с мешком монет. Клик по нему отбирает мешок.</summary>
public sealed class GoblinEvent : OverlayEvent
{
    private GoblinSettings _settings = new();
    private SpriteOverlay? _sprite;
    private Picture? _shown;
    private ScreenRect _area;
    private double _x;
    private int _y;
    private int _direction;
    private double _age;
    private bool _over;

    public override RandomEventInfo Info { get; } = new("goblin", HardshipResources.Screen);

    internal ScreenRect? GoblinBounds => Overlay.Invoke(() => _sprite is { IsVisible: true } sprite ? sprite.Bounds : (ScreenRect?)null);

    internal void ClickForTest() => Overlay.Invoke(() => OnClick(1, 1));

    protected override void OnStart() =>
        _settings = EventData.Load(Context.Data, "events/rewards", "goblin", new GoblinSettings(), Context.Log);

    protected override void Create()
    {
        _area = EventArea.WorkArea();
        _over = false;
        _age = 0;
        _shown = null;
        var width = EventPictures.GoblinRun1.Width * _settings.Scale;
        var height = EventPictures.GoblinRun1.Height * _settings.Scale;
        _direction = Random.Next(2) == 0 ? 1 : -1;
        _x = _direction > 0 ? _area.Left - width : _area.Right;

        // Бежит по нижней половине экрана, где реже лежат заголовки окон.
        _y = _area.Top + (_area.Height / 2) + Random.Next(Math.Max(1, (_area.Height / 2) - height));
        _sprite = Overlay.CreateSprite(OnClick);
        Draw(_sprite, Frame(), _settings.Scale, new ScreenPoint((int)_x, _y), ref _shown);
    }

    protected override void Tick(double seconds)
    {
        if (_sprite is null || _over)
        {
            return;
        }

        _age += seconds;
        _x += _direction * _settings.Speed * seconds;
        var width = EventPictures.GoblinRun1.Width * _settings.Scale;
        if ((_direction > 0 && _x > _area.Right) || (_direction < 0 && _x < _area.Left - width))
        {
            _over = true;
            _sprite.Hide();
            Complete(false, 0);
            return;
        }

        Draw(_sprite, Frame(), _settings.Scale, new ScreenPoint((int)_x, _y), ref _shown);
    }

    protected override void Finish() => _sprite = null;

    private Picture Frame()
    {
        var run = _age % 0.24 < 0.12 ? EventPictures.GoblinRun1 : EventPictures.GoblinRun2;
        return _direction > 0 ? run : Mirrors.Of(run);
    }

    private void OnClick(int x, int y)
    {
        if (_over || _sprite is null)
        {
            return;
        }

        _over = true;
        _sprite.Hide();
        var reward = Context.Economy.Earn(EventSources.Event, EventData.Between(Random, _settings.MinReward, _settings.MaxReward));
        Complete(true, reward);
    }
}

/// <summary>Настройки босса: сколько кликов выдерживает, сколько секунд ждёт и награда за победу.</summary>
public sealed record BossSettings(int Health = 30, double Seconds = 45, long Reward = 400, int Scale = 5);

/// <summary>
/// Босс: раз в день пиксельный монстр медленно плавает по экрану. Каждый клик отнимает одно очко здоровья,
/// полоска под ним показывает остаток. Победа приносит награду, а если не успеть, босс уходит.
/// </summary>
public sealed class BossEvent : OverlayEvent
{
    private const int BarHeight = 3;
    private const double HurtSeconds = 0.12;

    private BossSettings _settings = new();
    private SpriteOverlay? _sprite;
    private ScreenRect _area;
    private ScreenPoint _home;
    private double _age;
    private double _hurt;
    private int _health;
    private int _drawnHealth = -1;
    private bool _drawnHurt;
    private bool _over;

    public override RandomEventInfo Info { get; } = new("boss", HardshipResources.Screen);

    internal int Health => Overlay.Invoke(() => _health);

    internal ScreenRect? BossBounds => Overlay.Invoke(() => _sprite is { IsVisible: true } sprite ? sprite.Bounds : (ScreenRect?)null);

    internal void HitForTest() => Overlay.Invoke(() => OnClick(1, 1));

    protected override void OnStart()
    {
        _settings = EventData.Load(Context.Data, "events/rewards", "boss", new BossSettings(), Context.Log);
        if (_settings.Health < 1)
        {
            Context.Log.Error("Неверное здоровье босса, действует значение по умолчанию");
            _settings = _settings with { Health = new BossSettings().Health };
        }
    }

    protected override void Create()
    {
        _area = EventArea.WorkArea();
        _health = _settings.Health;
        _drawnHealth = -1;
        _age = 0;
        _hurt = 0;
        _over = false;
        var picture = Compose(false);
        _home = new ScreenPoint(
            _area.Left + ((_area.Width - (picture.Width * _settings.Scale)) / 2),
            _area.Top + ((_area.Height - (picture.Height * _settings.Scale)) / 3));
        _sprite = Overlay.CreateSprite(OnClick);
        Redraw();
    }

    protected override void Tick(double seconds)
    {
        if (_sprite is null || _over)
        {
            return;
        }

        _age += seconds;
        if (_age >= _settings.Seconds)
        {
            _over = true;
            _sprite.Hide();
            Context.Log.Info($"Босс ушёл, здоровья осталось {_health}");
            Complete(false, 0);
            return;
        }

        _hurt = Math.Max(0, _hurt - seconds);
        Redraw();
    }

    protected override void Finish() => _sprite = null;

    private void Redraw()
    {
        var hurt = _hurt > 0;
        var position = new ScreenPoint(
            _home.X + (int)(Math.Sin(_age * 0.7) * _area.Width / 5),
            _home.Y + (int)(Math.Sin(_age * 1.3) * _area.Height / 10));
        if (_health != _drawnHealth || hurt != _drawnHurt)
        {
            _drawnHealth = _health;
            _drawnHurt = hurt;
            var picture = Compose(hurt);
            _sprite!.Show(picture.Pixels, picture.Width, picture.Height, _settings.Scale, position);
        }
        else
        {
            _sprite!.MoveTo(position);
        }
    }

    /// <summary>Босс с полоской здоровья под ним.</summary>
    private Picture Compose(bool hurt)
    {
        var body = hurt ? EventPictures.BossHurt : EventPictures.BossIdle;
        var picture = new Picture(body.Width, body.Height + BarHeight + 1);
        for (var y = 0; y < body.Height; y++)
        {
            for (var x = 0; x < body.Width; x++)
            {
                picture[x, y] = body[x, y];
            }
        }

        var top = body.Height + 1;
        picture.Fill(0, top, body.Width, BarHeight, Colors.Ink);
        var filled = (int)Math.Ceiling((body.Width - 2) * (double)_health / _settings.Health);
        picture.Fill(1, top + 1, filled, BarHeight - 2, Colors.Red);
        return picture;
    }

    private void OnClick(int x, int y)
    {
        if (_over || _sprite is null)
        {
            return;
        }

        _health--;
        _hurt = HurtSeconds;
        if (_health > 0)
        {
            Redraw();
            return;
        }

        _over = true;
        _sprite.Hide();
        Complete(true, Context.Economy.Earn(EventSources.Event, _settings.Reward));
    }
}

/// <summary>Зеркальные копии картинок, по одной на картинку.</summary>
internal static class Mirrors
{
    private static readonly Dictionary<Picture, Picture> Cache = new(ReferenceEqualityComparer.Instance);
    private static readonly Lock Gate = new();

    public static Picture Of(Picture source)
    {
        lock (Gate)
        {
            if (!Cache.TryGetValue(source, out var mirrored))
            {
                mirrored = new Picture(source.Width, source.Height);
                for (var y = 0; y < source.Height; y++)
                {
                    for (var x = 0; x < source.Width; x++)
                    {
                        mirrored[source.Width - 1 - x, y] = source[x, y];
                    }
                }

                Cache[source] = mirrored;
            }

            return mirrored;
        }
    }
}
