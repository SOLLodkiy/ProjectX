using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Physics;

/// <summary>
/// Камнепад: с неба падают валуны и пролетают сквозь окна. Валун, попавший в курсор, сталкивает его, крупный —
/// сильнее. Валун, задевший заголовок окна, сдвигает окно вниз; после усложнения, аварийной остановки или сбоя окна
/// возвращаются на место.
/// </summary>
public sealed class RockfallHardship : OverlayHardship
{
    /// <summary>Настройка для проверок: двигать только окна программ из этой папки.</summary>
    public const string OnlyUnderSetting = "onlyUnder";

    /// <summary>На какую долю своего размера валун сдвигает окно.</summary>
    public const double WindowPush = 0.6;

    /// <summary>Дальше этого окно вниз не уезжает, сколько бы валунов ни попало.</summary>
    public const int MaxWindowShift = 260;

    private const int UserMoveTolerance = 6;

    private readonly Lock _lock = new();
    private readonly List<(Boulder Rock, Picture[] Frames, SpriteOverlay Sprite)> _rocks = [];
    private readonly Dictionary<IntPtr, PushedWindow> _windows = [];
    private readonly Knock _knock = new();
    private Random _random = new();
    private RockSettings _settings = new();
    private ScreenRect _area;
    private double _spawnIn;
    private IDisposable? _mark;
    private int _fallen;
    private int _cursorHits;

    public override HardshipInfo Info { get; } = new(
        "rockfall",
        HardshipLevel.Medium,
        HardshipResources.CursorMotion | HardshipResources.Screen | HardshipResources.Windows);

    /// <summary>Какие окна можно двигать. Проверки ограничивают усложнение своими окнами.</summary>
    internal Func<WindowInfo, bool> Scope { get; set; } = _ => true;

    /// <summary>Сколько валунов сейчас летит, для проверок.</summary>
    internal int Falling => Overlay.Invoke(() => _rocks.Count);

    /// <summary>Сколько раз валун попал в курсор.</summary>
    internal int CursorHits
    {
        get
        {
            lock (_lock)
            {
                return _cursorHits;
            }
        }
    }

    /// <summary>Сдвинутые окна и их исходные места, для проверок.</summary>
    internal IReadOnlyList<MovedWindow> Moved
    {
        get
        {
            lock (_lock)
            {
                return [.. _windows.Values.Select(window => window.Origin)];
            }
        }
    }

    protected override void OnStart()
    {
        var onlyUnder = Context.Settings.Get<string>(OnlyUnderSetting);
        if (string.IsNullOrWhiteSpace(onlyUnder))
        {
            return;
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(onlyUnder)) + Path.DirectorySeparatorChar;
        Scope = window => ProcessControl.Stamp(window.ProcessId) is { } stamp
            && ProcessControl.ImagePath(stamp) is { } path
            && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    protected override void Begin()
    {
        _random = new Random();
        var spread = new Spread(_random);
        var (minSize, maxSize) = spread.Vary(30, 80);
        var (minPause, maxPause) = spread.Vary(1, 3);
        lock (_lock)
        {
            _settings = new RockSettings(minSize, maxSize, minPause, maxPause, Gravity: spread.Vary(1100.0));
            _area = PhysicsArea.All();
            _spawnIn = 0.05;
            _fallen = 0;
            _cursorHits = 0;
            _knock.Reset();
            _windows.Clear();
            _mark = Context.Changes.Record(WindowPositionsRestorer.ChangeKind, WindowPositionsRestorer.ToData([]));
        }

        base.Begin();
    }

    protected override void End()
    {
        base.End();
        lock (_lock)
        {
            new WindowPositionsRestorer().Restore(WindowPositionsRestorer.ToData(_windows.Values.Select(window => window.Origin)));
            Context.Log.Info($"Камнепад: валунов {_fallen}, попаданий в курсор {_cursorHits}, возвращено окон {_windows.Count}");
            _windows.Clear();
            _mark?.Dispose();
            _mark = null;
        }
    }

    protected override void Create() => _rocks.Clear();

    protected override void Tick(double seconds)
    {
        _spawnIn -= seconds;
        if (_spawnIn <= 0)
        {
            _spawnIn = _settings.MinPause + (_random.NextDouble() * (_settings.MaxPause - _settings.MinPause));
            Spawn();
        }

        var held = CursorControl.AnyButtonDown();
        var cursor = Vec.From(CursorControl.Position());
        foreach (var item in _rocks.ToList())
        {
            item.Rock.Step(seconds, _settings);
            if (!held && item.Rock.Push(cursor, _settings) is { } push)
            {
                // Курсор выталкивается за край валуна и ещё немного летит от удара.
                cursor = push.Point;
                CursorControl.MoveTo(cursor.ToPoint());
                _knock.Add(push.Knock);
                lock (_lock)
                {
                    _cursorHits++;
                }
            }

            HitWindow(item.Rock);
            var frame = item.Frames[(int)(Math.Abs(item.Rock.Turn) / (Math.PI / 2)) % 4];
            item.Sprite.Show(frame.Pixels, frame.Width, frame.Height, RockArt.Scale, Corner(item.Rock, frame));
            if (item.Rock.Position.Y - item.Rock.Radius > _area.Bottom)
            {
                item.Sprite.Destroy();
                _rocks.Remove(item);
            }
        }

        if (held)
        {
            _knock.Reset();
            return;
        }

        var step = _knock.Step(seconds);
        if (step.X != 0 || step.Y != 0)
        {
            var position = CursorControl.Position();
            CursorControl.MoveTo(new ScreenPoint(position.X + step.X, position.Y + step.Y));
        }
    }

    /// <summary>Бросить валун прямо в точку, для проверок.</summary>
    internal void DropForTest(ScreenPoint point, double size) => Overlay.Invoke(() =>
    {
        var rock = new Boulder(new Vec(point.X, point.Y - (size * 2)), size, 0);
        var frames = RockArt.Frames(size, _random);
        _rocks.Add((rock, frames, Overlay.CreateSprite(null)));
        _spawnIn = 60;
    });

    private void Spawn()
    {
        var size = _settings.MinSize + (_random.NextDouble() * (_settings.MaxSize - _settings.MinSize));
        var x = _area.Left + (_random.NextDouble() * _area.Width);
        var rock = new Boulder(new Vec(x, _area.Top - size), size, (_random.NextDouble() - 0.5) * 80);
        _rocks.Add((rock, RockArt.Frames(size, _random), Overlay.CreateSprite(null)));
        lock (_lock)
        {
            _fallen++;
        }
    }

    private void HitWindow(Boulder rock)
    {
        var bottom = rock.Bottom.ToPoint();
        if (!_area.Contains(bottom))
        {
            return;
        }

        var window = WindowHit.TopWindowAt(bottom);
        if (window == IntPtr.Zero || rock.HitWindows.Contains(window) || WindowControl.Describe(window) is not { } info || !Fits(info))
        {
            return;
        }

        var bounds = WindowTraits.Bounds(window);
        if (bottom.Y < bounds.Top || bottom.Y > bounds.Top + WindowHit.CaptionHeight())
        {
            return;
        }

        rock.HitWindows.Add(window);
        lock (_lock)
        {
            if (!_windows.TryGetValue(window, out var pushed))
            {
                // Место окна записывается до первого сдвига: после сбоя сторож вернёт его.
                pushed = new PushedWindow(new MovedWindow(window.ToInt64(), info.ProcessId, bounds.Left, bounds.Top), bounds.Left, bounds.Top);
                _windows[window] = pushed;
                Remember();
            }
            else if (Math.Abs(bounds.Left - pushed.ShownX) > UserMoveTolerance || Math.Abs(bounds.Top - pushed.ShownY) > UserMoveTolerance)
            {
                // Человек сам передвинул окно: новое место становится исходным.
                pushed = new PushedWindow(pushed.Origin with { X = bounds.Left, Y = bounds.Top }, bounds.Left, bounds.Top);
                _windows[window] = pushed;
                Remember();
            }

            var y = Math.Min(pushed.Origin.Y + MaxWindowShift, bounds.Top + (int)Math.Round(rock.Size * WindowPush));
            if (y != bounds.Top)
            {
                WindowControl.MoveInBackground(window, new ScreenPoint(bounds.Left, y));
                _windows[window] = pushed with { ShownX = bounds.Left, ShownY = y };
            }
        }
    }

    private void Remember()
    {
        var previous = _mark;
        _mark = Context.Changes.Record(WindowPositionsRestorer.ChangeKind, WindowPositionsRestorer.ToData(_windows.Values.Select(window => window.Origin)));
        previous?.Dispose();
    }

    private bool Fits(WindowInfo window) =>
        window.ProcessId != Environment.ProcessId
        && !window.IsMinimized
        && !WindowControl.IsDesktop(window)
        && !WindowControl.IsMaximized(window.Handle)
        && Scope(window);

    private static ScreenPoint Corner(Boulder rock, Picture frame) =>
        new((int)Math.Round(rock.Position.X - (frame.Width * RockArt.Scale / 2.0)), (int)Math.Round(rock.Position.Y - (frame.Height * RockArt.Scale / 2.0)));

    private sealed record PushedWindow(MovedWindow Origin, int ShownX, int ShownY);
}
