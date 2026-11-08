using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Physics;

/// <summary>
/// Клон курсора: двигается как рука человека — плавно разгоняется и тормозит по слегка изогнутому пути,
/// замирает и снова двигается. Ничего не нажимает. Потоками управляет владелец.
/// </summary>
public sealed class CloneWalker
{
    public const double MinSpeed = 250;
    public const double MaxSpeed = 900;
    public const double MinPause = 0.2;
    public const double MaxPause = 1.6;

    private readonly Random _random;
    private readonly ScreenRect _screen;
    private Vec _start;
    private Vec _control;
    private Vec _end;
    private double _progress = 1;
    private double _duration;
    private double _pause;

    public CloneWalker(Random random, ScreenRect screen)
    {
        _random = random;
        _screen = screen;
        Position = new Vec(screen.Left + (random.NextDouble() * screen.Width), screen.Top + (random.NextDouble() * screen.Height));
        _pause = random.NextDouble() * MaxPause;
    }

    public Vec Position { get; private set; }

    public void Step(double seconds)
    {
        if (_pause > 0)
        {
            _pause -= seconds;
            if (_pause <= 0)
            {
                Plan();
            }

            return;
        }

        _progress = Math.Min(1, _progress + (seconds / _duration));

        // Плавный разгон и торможение, как у руки.
        var t = _progress * _progress * (3 - (2 * _progress));
        Position = PhysicsArea.Clamp(_screen, (_start * ((1 - t) * (1 - t))) + (_control * (2 * (1 - t) * t)) + (_end * (t * t)));
        if (_progress >= 1)
        {
            _pause = MinPause + (_random.NextDouble() * (MaxPause - MinPause));
        }
    }

    private void Plan()
    {
        _start = Position;
        var distance = 60 + (_random.NextDouble() * 520);
        var angle = _random.NextDouble() * Math.PI * 2;
        _end = PhysicsArea.Clamp(_screen, _start + (Vec.FromAngle(angle) * distance));
        var middle = (_start + _end) / 2;
        var side = new Vec(-(_end - _start).Y, (_end - _start).X) * ((_random.NextDouble() - 0.5) * 0.5);
        _control = middle + side;
        var speed = MinSpeed + (_random.NextDouble() * (MaxSpeed - MinSpeed));
        _duration = Math.Max(0.15, (_end - _start).Length / speed);
        _progress = 0;
    }
}

/// <summary>Толпа клонов: по каждому монитору ходят курсоры, неотличимые от курсора человека.</summary>
public sealed class ClonesHardship : OverlayHardship
{
    public const int MinPerScreen = 8;
    public const int MaxPerScreen = 12;

    private readonly List<(CloneWalker Walker, SpriteOverlay Sprite)> _clones = [];
    private CursorImage _arrow = ArrowPictures.Fallback();
    private int _perScreen;

    public override HardshipInfo Info { get; } = new("clones", HardshipLevel.Easy, HardshipResources.Screen);

    /// <summary>Сколько клонов на экране, для проверок.</summary>
    internal int Count => Overlay.Invoke(() => _clones.Count);

    protected override void Begin()
    {
        var random = new Random();
        var spread = new Spread(random);
        _perScreen = Math.Clamp(spread.Vary(random.Next(MinPerScreen, MaxPerScreen + 1)), MinPerScreen, MaxPerScreen);
        _arrow = ArrowPictures.Current();
        base.Begin();
    }

    protected override void Create()
    {
        var random = new Random();
        _clones.Clear();
        foreach (var screen in PhysicsArea.Screens())
        {
            for (var i = 0; i < _perScreen; i++)
            {
                var walker = new CloneWalker(random, screen);
                var sprite = Overlay.CreateSprite(null);
                sprite.Show(_arrow.Pixels, _arrow.Width, _arrow.Height, 1, Corner(walker.Position));
                _clones.Add((walker, sprite));
            }
        }
    }

    protected override void Tick(double seconds)
    {
        foreach (var (walker, sprite) in _clones)
        {
            walker.Step(seconds);
            sprite.MoveTo(Corner(walker.Position));
        }
    }

    protected override void Finish()
    {
        Context.Log.Info($"Толпа клонов: {_clones.Count}");
        _clones.Clear();
    }

    private ScreenPoint Corner(Vec position) =>
        new((int)Math.Round(position.X) - _arrow.HotX, (int)Math.Round(position.Y) - _arrow.HotY);
}
