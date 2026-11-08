using System.Text.Json;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Safety;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Screen;

/// <summary>Окно, сдвинутое усложнением, и где оно стояло. Процесс запоминается: система повторно использует номера окон.</summary>
public sealed record MovedWindow(long Handle, int ProcessId, int X, int Y);

/// <summary>
/// Бродячие окна: активное окно потихоньку уползает в сторону, но не дальше <see cref="MaxOffset"/> пикселей.
/// После усложнения все сдвинутые окна возвращаются на место. Развёрнутые, свёрнутые и полноэкранные окна
/// и окна ADHDWindows не трогаются; пока нажата кнопка мыши, окна стоят — человек может их перетаскивать.
/// </summary>
public sealed class WanderingWindowsHardship : HardshipModule
{
    public const double Speed = 12;
    public const int MaxOffset = 70;

    /// <summary>Сдвиг окна больше этого между шагами сделал человек, а не усложнение.</summary>
    private const int UserMoveTolerance = 6;

    private readonly Lock _lock = new();
    private readonly Dictionary<IntPtr, Wanderer> _windows = [];
    private readonly Random _random = new();
    private IDisposable? _mark;
    private Ticker? _ticker;

    public override HardshipInfo Info { get; } = new("wanderingWindows", HardshipLevel.Medium, HardshipResources.Windows);

    /// <summary>Настройка для проверок: трогать только окна программ из этой папки.</summary>
    public const string OnlyUnderSetting = "onlyUnder";

    /// <summary>Какие окна можно двигать. Проверки ограничивают усложнение своими окнами.</summary>
    internal Func<WindowInfo, bool> Scope { get; set; } = _ => true;

    /// <summary>Сдвинутые окна, для проверок.</summary>
    internal IReadOnlyList<MovedWindow> Moved
    {
        get
        {
            lock (_lock)
            {
                return [.. _windows.Values.Select(item => item.Origin)];
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
        lock (_lock)
        {
            _windows.Clear();
            _mark = Context.Changes.Record(WindowPositionsRestorer.ChangeKind, WindowPositionsRestorer.ToData([]));
        }

        _ticker = new Ticker("WanderingWindows", Step, error => Context.Log.Error("Ошибка бродячих окон", error), intervalMs: 50);
    }

    protected override void End()
    {
        _ticker?.Dispose();
        _ticker = null;
        lock (_lock)
        {
            new WindowPositionsRestorer().Restore(WindowPositionsRestorer.ToData(_windows.Values.Select(item => item.Origin)));
            Context.Log.Info($"Бродячие окна: возвращено окон {_windows.Count}");
            _windows.Clear();
            _mark?.Dispose();
            _mark = null;
        }
    }

    private void Step(double seconds)
    {
        lock (_lock)
        {
            var foreground = WindowControl.Foreground();
            if (foreground == IntPtr.Zero
                || CursorControl.AnyButtonDown()
                || WindowControl.Describe(foreground) is not { } info
                || !Fits(info))
            {
                return;
            }

            var bounds = WindowTraits.Bounds(foreground);
            if (!_windows.TryGetValue(foreground, out var wanderer))
            {
                wanderer = new Wanderer(new MovedWindow(foreground.ToInt64(), info.ProcessId, bounds.Left, bounds.Top), _random.NextDouble() * Math.PI * 2);
                _windows[foreground] = wanderer;

                // Место окна записывается до первого сдвига: после сбоя сторож вернёт его.
                var previous = _mark;
                _mark = Context.Changes.Record(WindowPositionsRestorer.ChangeKind, WindowPositionsRestorer.ToData(_windows.Values.Select(item => item.Origin)));
                previous?.Dispose();
            }
            else if (Math.Abs(bounds.Left - wanderer.ShownX) > UserMoveTolerance || Math.Abs(bounds.Top - wanderer.ShownY) > UserMoveTolerance)
            {
                // Небольшая разница — это ещё не применённый сдвиг: окно двигается без ожидания программы.
                // Человек сам передвинул окно: новое место становится исходным, а окно и после усложнения останется там.
                wanderer.Rebase(bounds.Left, bounds.Top);
                var previous = _mark;
                _mark = Context.Changes.Record(WindowPositionsRestorer.ChangeKind, WindowPositionsRestorer.ToData(_windows.Values.Select(item => item.Origin)));
                previous?.Dispose();
            }

            var target = wanderer.Step(seconds, _random);
            if (target.X != bounds.Left || target.Y != bounds.Top)
            {
                WindowControl.MoveInBackground(foreground, target);
            }
        }
    }

    private bool Fits(WindowInfo window) =>
        window.ProcessId != Environment.ProcessId
        && !window.IsMinimized
        && !WindowControl.IsDesktop(window)
        && !WindowControl.IsMaximized(window.Handle)
        && !Desktop.IsForegroundFullscreen()
        && Scope(window);

    private sealed class Wanderer(MovedWindow origin, double heading)
    {
        private double _heading = heading;
        private double _offsetX;
        private double _offsetY;

        public MovedWindow Origin { get; private set; } = origin;

        public int ShownX { get; private set; } = origin.X;

        public int ShownY { get; private set; } = origin.Y;

        public ScreenPoint Step(double seconds, Random random)
        {
            _heading += (random.NextDouble() - 0.5) * seconds * 4;
            _offsetX += Math.Cos(_heading) * Speed * seconds;
            _offsetY += Math.Sin(_heading) * Speed * seconds;
            var distance = Math.Sqrt((_offsetX * _offsetX) + (_offsetY * _offsetY));
            if (distance > MaxOffset)
            {
                // У границы окно разворачивается обратно к своему месту.
                _offsetX *= MaxOffset / distance;
                _offsetY *= MaxOffset / distance;
                _heading = Math.Atan2(-_offsetY, -_offsetX);
            }

            ShownX = Origin.X + (int)Math.Round(_offsetX);
            ShownY = Origin.Y + (int)Math.Round(_offsetY);
            return new ScreenPoint(ShownX, ShownY);
        }

        public void Rebase(int x, int y)
        {
            Origin = Origin with { X = x, Y = y };
            _offsetX = 0;
            _offsetY = 0;
            ShownX = x;
            ShownY = y;
        }
    }
}

/// <summary>Возвращает окна, сдвинутые усложнением, на их места, если они принадлежат тем же процессам.</summary>
public sealed class WindowPositionsRestorer : ISystemRestorer
{
    public const string ChangeKind = "hardships.windowPositions";

    public string Kind => ChangeKind;

    public static string ToData(IEnumerable<MovedWindow> windows) => JsonSerializer.Serialize(windows.ToList());

    public void Restore(string data)
    {
        foreach (var window in JsonSerializer.Deserialize<List<MovedWindow>>(data) ?? [])
        {
            var handle = new IntPtr(window.Handle);
            if (WindowControl.BelongsTo(handle, window.ProcessId) && !WindowControl.IsMinimized(handle) && !WindowControl.IsMaximized(handle))
            {
                WindowControl.MoveInBackground(handle, new ScreenPoint(window.X, window.Y));
            }
        }
    }
}
