using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Screen;

/// <summary>
/// Муха: по экрану летает и ползает пиксельная муха. Если её прихлопнуть кликом, она приносит монетки,
/// а через несколько секунд прилетает новая. Клики мимо мухи проходят к окнам под ней.
/// </summary>
public sealed class FlyHardship : OverlayHardship
{
    /// <summary>Источник дохода для награды за муху: тот же, что у бонуса за пережитое усложнение.</summary>
    public const string IncomeSource = "hardship";

    public const decimal Bonus = 5;
    public const int Scale = 4;
    public const double Speed = 150;
    public const double SplatSeconds = 1;
    public const double RespawnSeconds = 3;

    private static readonly Picture Up = Picture.FromRows(
        [
            "..W...W..",
            ".WMW.WMW.",
            "..WKKKW..",
            ".RKKKKKR.",
            "..KKKKK..",
            "...KKK...",
            "..K...K..",
        ],
        Colors.Keys).Outlined(Colors.Mist);

    private static readonly Picture Down = Picture.FromRows(
        [
            ".........",
            ".........",
            "WWKKKKKWW",
            "MRKKKKKRM",
            "..KKKKK..",
            "...KKK...",
            "..K...K..",
        ],
        Colors.Keys).Outlined(Colors.Mist);

    private static readonly Picture Splat = Picture.FromRows(
        [
            "..K...K..",
            ".K.KgK.K.",
            "..KgGgK..",
            "KKgGGGgKK",
            "..KgGgK..",
            ".K.KgK.K.",
            "..K...K..",
        ],
        Colors.Keys).Outlined(Colors.Mist);

    private readonly Random _random = new();
    private readonly Lock _lock = new();
    private SpriteOverlay? _sprite;
    private ScreenRect _screen;
    private double _x;
    private double _y;
    private double _heading;
    private double _turnIn;
    private double _restFor;
    private double _flap;
    private double _splatFor;
    private double _respawnIn;
    private Picture? _shown;
    private int _swatted;

    public override HardshipInfo Info { get; } = new("fly", HardshipLevel.Easy, HardshipResources.Screen);

    /// <summary>Сколько мух прихлопнуто за это усложнение.</summary>
    internal int Swatted
    {
        get
        {
            lock (_lock)
            {
                return _swatted;
            }
        }
    }

    /// <summary>Где сейчас муха на экране, для проверок. Null — мухи нет.</summary>
    internal ScreenRect? FlyBounds => Overlay.Invoke(() => _sprite is { IsVisible: true } sprite && _splatFor <= 0 ? sprite.Bounds : (ScreenRect?)null);

    protected override void Create()
    {
        _swatted = 0;
        _sprite = Overlay.CreateSprite(OnClick);
        Spawn();
    }

    protected override void Tick(double seconds)
    {
        if (_sprite is null)
        {
            return;
        }

        if (_splatFor > 0)
        {
            _splatFor -= seconds;
            if (_splatFor <= 0)
            {
                _sprite.Hide();
                _shown = null;
                _respawnIn = RespawnSeconds;
            }

            return;
        }

        if (_respawnIn > 0)
        {
            _respawnIn -= seconds;
            if (_respawnIn <= 0)
            {
                Spawn();
            }

            return;
        }

        Fly(seconds);
    }

    protected override void Finish()
    {
        _sprite = null;
        lock (_lock)
        {
            Context.Log.Info($"Муха: прихлопнуто {_swatted}");
        }
    }

    /// <summary>Прихлопнуть муху, для проверок: как клик по ней.</summary>
    internal void SwatForTest() => Overlay.Invoke(() => OnClick(4, 4));

    private void Spawn()
    {
        _screen = OverlayArea.WorkArea();
        var width = Up.Width * Scale;
        var height = Up.Height * Scale;

        // Муха прилетает от края монитора.
        _x = _random.Next(2) == 0 ? _screen.Left : _screen.Right - width;
        _y = _screen.Top + _random.Next(Math.Max(1, _screen.Height - height));
        _heading = _x <= _screen.Left ? (_random.NextDouble() - 0.5) : Math.PI + (_random.NextDouble() - 0.5);
        _turnIn = 0.5;
        _restFor = 0;
        Show(Up);
    }

    private void Fly(double seconds)
    {
        if (_restFor > 0)
        {
            // Сидит и не машет крыльями.
            _restFor -= seconds;
            Show(Down);
            return;
        }

        _turnIn -= seconds;
        if (_turnIn <= 0)
        {
            _turnIn = 0.4 + (_random.NextDouble() * 0.8);
            _heading += (_random.NextDouble() - 0.5) * 2;
            if (_random.NextDouble() < 0.2)
            {
                _restFor = 1 + _random.NextDouble();
            }
        }

        var width = Up.Width * Scale;
        var height = Up.Height * Scale;
        _x += Math.Cos(_heading) * Speed * seconds;
        _y += Math.Sin(_heading) * Speed * seconds;

        // От края монитора муха отлетает обратно.
        if (_x < _screen.Left || _x > _screen.Right - width)
        {
            _heading = Math.PI - _heading;
            _x = Math.Clamp(_x, _screen.Left, _screen.Right - width);
        }

        if (_y < _screen.Top || _y > _screen.Bottom - height)
        {
            _heading = -_heading;
            _y = Math.Clamp(_y, _screen.Top, _screen.Bottom - height);
        }

        _flap += seconds;
        Show(_flap % 0.16 < 0.08 ? Up : Down);
    }

    private void Show(Picture picture)
    {
        var position = new ScreenPoint((int)_x, (int)_y);
        if (picture != _shown)
        {
            _shown = picture;
            _sprite!.Show(picture.Pixels, picture.Width, picture.Height, Scale, position);
        }
        else
        {
            _sprite!.MoveTo(position);
        }
    }

    private void OnClick(int x, int y)
    {
        if (_splatFor > 0 || _respawnIn > 0 || _sprite is null)
        {
            return;
        }

        _splatFor = SplatSeconds;
        _shown = Splat;
        _sprite.Show(Splat.Pixels, Splat.Width, Splat.Height, Scale, _sprite.Position);
        lock (_lock)
        {
            _swatted++;
        }

        Context.Economy.Earn(IncomeSource, Bonus);
    }
}
