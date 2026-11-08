using System.Globalization;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Basic;

/// <summary>Сильная гравитация: курсор всё время немного тянет к низу экрана.</summary>
public sealed class GravityHardship : HardshipModule
{
    /// <summary>Настройка: скорость падения в пикселях в секунду.</summary>
    public const string PullSetting = "pull";

    public const double DefaultPull = 40;

    private Ticker? _ticker;
    private double _carry;

    public override HardshipInfo Info { get; } = new("gravity", HardshipLevel.Easy, HardshipResources.CursorMotion);

    protected override void Begin()
    {
        var pull = Context.Settings.Get(PullSetting, DefaultPull);
        pull = pull is > 0 and <= 400 ? pull : DefaultPull;
        _carry = 0;
        _ticker = new Ticker("Gravity", seconds => Pull(pull, seconds), error => Context.Log.Error("Ошибка гравитации", error));
    }

    protected override void End()
    {
        _ticker?.Dispose();
        _ticker = null;
    }

    private void Pull(double pull, double seconds)
    {
        // Пока кнопка нажата, курсор не трогается: иначе гравитация испортит выделение или перетаскивание.
        if (CursorControl.AnyButtonDown())
        {
            _carry = 0;
            return;
        }

        _carry += pull * seconds;
        var step = (int)_carry;
        if (step == 0)
        {
            return;
        }

        _carry -= step;
        var position = CursorControl.Position();
        CursorControl.MoveTo(new ScreenPoint(position.X, position.Y + step));
    }
}

/// <summary>Невидимый курсор: все системные курсоры заменяются прозрачными. Откат — схема курсоров пользователя.</summary>
public sealed class InvisibleCursorHardship : HardshipModule
{
    private IDisposable? _mark;

    public override HardshipInfo Info { get; } = new("invisible", HardshipLevel.Hard, HardshipResources.CursorLook);

    protected override void Begin()
    {
        _mark = Context.Changes.Record(CursorRestorer.ChangeKind, string.Empty);
        var size = CursorControl.SystemCursorSize();
        CursorControl.ReplaceCursors(CursorArt.Blank(size), size, 0, 0, allKinds: true);
    }

    protected override void End()
    {
        CursorControl.RestoreCursors();
        _mark?.Dispose();
        _mark = null;
    }
}

/// <summary>
/// Загрязнение: курсор пачкается от движения и переходов между сайтами и окнами, грязный курсор медленнее.
/// Отряхнуть — быстро подвигать им из стороны в сторону. Меняет вид стрелки и скорость указателя, оба изменения
/// записаны в реестр изменений.
/// </summary>
public sealed class PollutionHardship : HardshipModule
{
    private const double TitleCheckSeconds = 0.5;

    private readonly Lock _lock = new();
    private DirtMeter _dirt = new();
    private IDisposable? _cursorMark;
    private IDisposable? _speedMark;
    private Ticker? _ticker;
    private int _originalSpeed;
    private int _appliedSpeed;
    private int _appliedStep = -1;
    private int _size;
    private ScreenPoint _last;
    private double _sinceTitleCheck;
    private IntPtr _lastWindow;
    private string _lastTitle = string.Empty;

    public override HardshipInfo Info { get; } = new("pollution", HardshipLevel.Medium, HardshipResources.CursorMotion | HardshipResources.CursorLook);

    /// <summary>Текущая грязь, для проверок.</summary>
    internal double Dirt
    {
        get
        {
            lock (_lock)
            {
                return _dirt.Level;
            }
        }
    }

    protected override void Begin()
    {
        lock (_lock)
        {
            _dirt = new DirtMeter();
            _originalSpeed = CursorControl.Speed();
            _appliedSpeed = _originalSpeed;
            _appliedStep = -1;
            _size = CursorControl.SystemCursorSize();
            _last = CursorControl.Position();
            _lastWindow = WindowControl.Foreground();
            _lastTitle = WindowControl.Title(_lastWindow);
            _speedMark = Context.Changes.Record(MouseSpeedRestorer.ChangeKind, _originalSpeed.ToString(CultureInfo.InvariantCulture));
            _cursorMark = Context.Changes.Record(CursorRestorer.ChangeKind, string.Empty);
            Apply();
        }

        _ticker = new Ticker("Pollution", Step, error => Context.Log.Error("Ошибка загрязнения", error));
    }

    protected override void End()
    {
        _ticker?.Dispose();
        _ticker = null;
        lock (_lock)
        {
            Context.Log.Info($"Загрязнение: курсор отряхнули {_dirt.Shakes} раз");
            CursorControl.SetSpeed(_originalSpeed);
            CursorControl.RestoreCursors();
            _speedMark?.Dispose();
            _cursorMark?.Dispose();
            _speedMark = null;
            _cursorMark = null;
        }
    }

    private void Step(double seconds)
    {
        lock (_lock)
        {
            var position = CursorControl.Position();
            _dirt.Move(position.X - _last.X, position.Y - _last.Y, seconds);
            _last = position;

            // Переход на другой сайт меняет заголовок окна браузера, переход в другую программу — активное окно.
            _sinceTitleCheck += seconds;
            if (_sinceTitleCheck >= TitleCheckSeconds)
            {
                _sinceTitleCheck = 0;
                var window = WindowControl.Foreground();
                var title = WindowControl.Title(window);
                if (window != _lastWindow || title != _lastTitle)
                {
                    _lastWindow = window;
                    _lastTitle = title;
                    _dirt.Transition();
                }
            }

            Apply();
        }
    }

    private void Apply()
    {
        var step = _dirt.Step();
        if (step != _appliedStep)
        {
            _appliedStep = step;
            CursorControl.ReplaceCursors(CursorArt.DirtyArrow(_size, step / 8.0), _size, 0, 0, allKinds: false);
        }

        var speed = _dirt.SpeedFor(_originalSpeed);
        if (speed != _appliedSpeed)
        {
            _appliedSpeed = speed;
            CursorControl.SetSpeed(speed);
        }
    }
}
