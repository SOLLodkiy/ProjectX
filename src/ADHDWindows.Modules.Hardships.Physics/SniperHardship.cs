using System.Diagnostics;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Physics;

/// <summary>
/// Снайпер: прицел крадётся по следу курсора. Если он догнал курсор и курсор не ушёл за 2 секунды, снайпер
/// стреляет: вспышка, 8-битный выстрел, курсор 5 секунд не двигается, а прицел пропадает на 7 секунд.
/// </summary>
public sealed class SniperHardship : OverlayHardship
{
    public const int Scale = 3;
    public const double FlashSeconds = 0.18;
    public const string ShotSound = "sounds/sniper-shot.wav";

    private static readonly string[] SightRows =
    [
        "....KKKKK....",
        "..KK..C..KK..",
        ".K....C....K.",
        ".K.........K.",
        "K...........K",
        "K...........K",
        "KCC...C...CCK",
        "K...........K",
        "K...........K",
        ".K.........K.",
        ".K....C....K.",
        "..KK..C..KK..",
        "....KKKKK....",
    ];

    private static readonly string[] FlashRows =
    [
        "...W.....W...",
        "....W...W....",
        ".....YYY.....",
        "W...YWWWY...W",
        ".W.YWWWWWY.W.",
        "..YWWWWWWWY..",
        "..YWWWWWWWY..",
        "..YWWWWWWWY..",
        ".W.YWWWWWY.W.",
        "W...YWWWY...W",
        ".....YYY.....",
        "....W...W....",
        "...W.....W...",
    ];

    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = new();
    private readonly Picture _calm = Sight(Colors.Ink, Colors.White);
    private readonly Picture _aiming = Sight(Colors.RedDark, Colors.Red);
    private readonly Picture _flash = Picture.FromRows(FlashRows, Colors.Keys);
    private SniperSight? _sniper;
    private MouseMoveFilter? _filter;
    private SpriteOverlay? _sightSprite;
    private SpriteOverlay? _flashSprite;
    private QuietWatch? _quiet;
    private Picture? _shown;
    private double _flashFor;
    private string? _sound;

    public override HardshipInfo Info { get; } = new(
        "sniper",
        HardshipLevel.Medium,
        HardshipResources.CursorMotion | HardshipResources.Screen | HardshipResources.Sound);

    /// <summary>Снайпер, для проверок.</summary>
    internal SniperSight? Sniper
    {
        get
        {
            lock (_lock)
            {
                return _sniper;
            }
        }
    }

    /// <summary>Подмена проигрывания для проверок: выстрел в автотестах не слышен.</summary>
    internal Func<string, bool> Play { get; init; } = Sounds.Play;

    /// <summary>Сколько раз выстрел прозвучал (не прозвучал при «Не беспокоить»).</summary>
    internal int SoundsPlayed { get; private set; }

    protected override void OnStart()
    {
        _quiet = new QuietWatch(Context.Events);
        _sound = AssetFiles.Find(ShotSound);
        if (_sound is null)
        {
            Context.Log.Warn("Звук выстрела не найден: снайпер стреляет беззвучно");
        }
    }

    protected override void Begin()
    {
        var spread = new Spread(new Random());
        lock (_lock)
        {
            _sniper = new SniperSight(new SniperSettings(Delay: spread.Vary(0.9), LockRadius: spread.Vary(10.0)));
            _clock.Restart();
            _flashFor = 0;
            _shown = null;
            SoundsPlayed = 0;
        }

        base.Begin();
        _filter = new MouseMoveFilter(Freeze, Context.Log);
        if (!_filter.Start())
        {
            throw new InvalidOperationException("Фильтр движения для снайпера не поставлен");
        }
    }

    protected override void End()
    {
        _filter?.Dispose();
        _filter = null;
        base.End();
    }

    protected override void Create()
    {
        _flashSprite = Overlay.CreateSprite(null);
        _sightSprite = Overlay.CreateSprite(null);
    }

    protected override void Tick(double seconds)
    {
        bool shot;
        Vec sight;
        bool visible;
        SniperState state;
        lock (_lock)
        {
            if (_sniper is null)
            {
                return;
            }

            shot = _sniper.Step(_clock.Elapsed.TotalSeconds, Vec.From(CursorControl.Position()));
            sight = _sniper.Sight;
            visible = _sniper.SightVisible;
            state = _sniper.State;
        }

        if (shot)
        {
            Shoot(sight);
        }

        if (_flashFor > 0)
        {
            _flashFor -= seconds;
            if (_flashFor <= 0)
            {
                _flashSprite?.Hide();
            }
        }

        if (!visible)
        {
            _sightSprite?.Hide();
            _shown = null;
            return;
        }

        var picture = state == SniperState.Aiming ? _aiming : _calm;
        var corner = Corner(sight, picture);
        if (picture != _shown || _sightSprite?.IsVisible != true)
        {
            _shown = picture;
            _sightSprite?.Show(picture.Pixels, picture.Width, picture.Height, Scale, corner);
        }
        else
        {
            _sightSprite.MoveTo(corner);
        }
    }

    protected override void Finish()
    {
        lock (_lock)
        {
            Context.Log.Info($"Снайпер: выстрелов {_sniper?.Shots ?? 0}");
            _sniper = null;
        }

        _sightSprite = null;
        _flashSprite = null;
    }

    /// <summary>Решение фильтра движения без хука, для проверок: курсор после выстрела стоит на месте.</summary>
    internal ScreenPoint? Freeze(ScreenPoint current, ScreenPoint target)
    {
        lock (_lock)
        {
            return _sniper is not null && _sniper.IsFrozen(_clock.Elapsed.TotalSeconds) ? current : null;
        }
    }

    private void Shoot(Vec at)
    {
        _flashFor = FlashSeconds;
        _flashSprite?.Show(_flash.Pixels, _flash.Width, _flash.Height, Scale, Corner(at, _flash));
        if (_sound is not null && _quiet?.IsQuiet != true && Play(_sound))
        {
            SoundsPlayed++;
        }
    }

    private static ScreenPoint Corner(Vec center, Picture picture) =>
        new((int)Math.Round(center.X - (picture.Width * Scale / 2.0)), (int)Math.Round(center.Y - (picture.Height * Scale / 2.0)));

    private static Picture Sight(uint ring, uint cross)
    {
        var colors = new Dictionary<char, uint>(Colors.Keys) { ['K'] = ring, ['C'] = cross };
        return Picture.FromRows(SightRows, colors).Outlined(Colors.Mist);
    }
}
