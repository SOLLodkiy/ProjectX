using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Physics;

/// <summary>Курсор на цепи: к курсору привязан второй, красный, курсор бота; бот бродит, делает рывки и тянет за цепь.</summary>
public sealed class ChainHardship : OverlayHardship
{
    public const int ChainScale = 2;
    public const double LinkSpacing = 9;

    private readonly Lock _lock = new();
    private ChainBot? _bot;
    private SpriteOverlay? _botSprite;
    private SpriteOverlay? _chainSprite;
    private CursorImage _arrow = ArrowPictures.Fallback();
    private ScreenRect _area;
    private Vec? _expected;
    private Vec _carry;
    private int _pulls;

    public override HardshipInfo Info { get; } = new("chain", HardshipLevel.Medium, HardshipResources.CursorMotion | HardshipResources.Screen);

    /// <summary>Бот, для проверок.</summary>
    internal ChainBot? Bot
    {
        get
        {
            lock (_lock)
            {
                return _bot;
            }
        }
    }

    protected override void Begin()
    {
        var spread = new Spread(new Random());
        var random = new Random();
        var cursor = Vec.From(CursorControl.Position());
        var (minPause, maxPause) = spread.Vary(4, 8);
        var settings = new ChainSettings(
            Length: spread.Vary(200.0),
            WanderSpeed: spread.Vary(90.0),
            DashSpeed: spread.Vary(1100.0),
            MinDashPause: minPause,
            MaxDashPause: maxPause);
        lock (_lock)
        {
            _area = PhysicsArea.All();
            _bot = new ChainBot(random, settings, PhysicsArea.Clamp(_area, cursor + (Vec.FromAngle(random.NextDouble() * Math.PI * 2) * (settings.Length * 0.6))));
            _arrow = ArrowPictures.Tinted(ArrowPictures.Current(), Colors.RedDark, Colors.Red);
            _expected = null;
            _carry = Vec.Zero;
            _pulls = 0;
        }

        base.Begin();
    }

    protected override void Create()
    {
        _chainSprite = Overlay.CreateSprite(null);
        _botSprite = Overlay.CreateSprite(null);
        _botSprite.Show(_arrow.Pixels, _arrow.Width, _arrow.Height, 1, new ScreenPoint(-10000, -10000));
    }

    protected override void Tick(double seconds)
    {
        Vec bot;
        Vec user;
        double length;
        lock (_lock)
        {
            if (_bot is null)
            {
                return;
            }

            user = Vec.From(CursorControl.Position());
            var held = CursorControl.AnyButtonDown();
            var hand = _expected is { } expected ? (user - expected) / Math.Max(seconds, 1e-3) : Vec.Zero;
            var shift = _bot.Step(seconds, user, hand, held, _area);
            if (!held)
            {
                _carry += shift;
                var step = new ScreenPoint((int)_carry.X, (int)_carry.Y);
                if (step.X != 0 || step.Y != 0)
                {
                    _carry -= new Vec(step.X, step.Y);
                    CursorControl.MoveTo(new ScreenPoint(user.ToPoint().X + step.X, user.ToPoint().Y + step.Y));
                    user = Vec.From(CursorControl.Position());
                    _pulls++;
                }
            }
            else
            {
                _carry = Vec.Zero;
            }

            _expected = user;
            bot = _bot.Position;
            length = _bot.Length;
        }

        _botSprite?.MoveTo(new ScreenPoint((int)Math.Round(bot.X) - _arrow.HotX, (int)Math.Round(bot.Y) - _arrow.HotY));
        DrawChain(user, bot, length);
    }

    protected override void Finish()
    {
        lock (_lock)
        {
            Context.Log.Info($"Курсор на цепи: рывков {_bot?.Dashes ?? 0}, сдвигов курсора {_pulls}");
            _bot = null;
        }

        _botSprite = null;
        _chainSprite = null;
    }

    private void DrawChain(Vec user, Vec bot, double length)
    {
        if (_chainSprite is null)
        {
            return;
        }

        var points = ChainLinks.Points(user, bot, length, LinkSpacing);
        var left = points.Min(point => point.X) - 6;
        var top = points.Min(point => point.Y) - 6;
        var width = (int)Math.Ceiling((points.Max(point => point.X) - left + 6) / ChainScale) + 1;
        var height = (int)Math.Ceiling((points.Max(point => point.Y) - top + 6) / ChainScale) + 1;
        var picture = new Picture(width, height);
        for (var i = 1; i < points.Count - 1; i++)
        {
            var x = (int)((points[i].X - left) / ChainScale);
            var y = (int)((points[i].Y - top) / ChainScale);

            // Звенья через одно повёрнуты: кольцо поперёк и кольцо вдоль.
            if (i % 2 == 0)
            {
                Ring(picture, x - 2, y - 1, 5, 3);
            }
            else
            {
                Ring(picture, x - 1, y - 2, 3, 5);
            }
        }

        _chainSprite.Show(picture.Pixels, picture.Width, picture.Height, ChainScale, new ScreenPoint((int)left, (int)top));
    }

    private static void Ring(Picture picture, int x, int y, int width, int height)
    {
        picture.Fill(x - 1, y - 1, width + 2, height + 2, Colors.Ink);
        picture.Fill(x, y, width, height, Colors.Mist);
        picture.Fill(x + 1, y + 1, width - 2, height - 2, Colors.Ink);
    }
}

/// <summary>Картинки стрелки для клонов и бота.</summary>
public static class ArrowPictures
{
    /// <summary>Стрелка человека, как её рисует система, или пиксельная стрелка, если прочитать не удалось.</summary>
    public static CursorImage Current()
    {
        try
        {
            if (CursorControl.ArrowImage() is { } image && image.Pixels.Any(pixel => (pixel >> 24) != 0))
            {
                return image;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Картинку курсора не прочитать — подойдёт и нарисованная.
        }

        return Fallback();
    }

    /// <summary>Пиксельная стрелка программы размером с системный курсор.</summary>
    public static CursorImage Fallback()
    {
        var size = 32;
        var (pixels, hotX, hotY) = CursorArt.Arrow(size, 1);
        return new CursorImage(pixels, size, size, hotX, hotY);
    }

    /// <summary>Та же стрелка, перекрашенная: тёмные точки в <paramref name="dark"/>, светлые в <paramref name="light"/>.</summary>
    public static CursorImage Tinted(CursorImage image, uint dark, uint light)
    {
        var pixels = new uint[image.Pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var pixel = image.Pixels[i];
            var alpha = pixel >> 24;
            if (alpha == 0)
            {
                continue;
            }

            var brightness = (((pixel >> 16) & 0xFF) + ((pixel >> 8) & 0xFF) + (pixel & 0xFF)) / 3;
            pixels[i] = (alpha << 24) | ((brightness > 127 ? light : dark) & 0x00FFFFFF);
        }

        return image with { Pixels = pixels };
    }
}
