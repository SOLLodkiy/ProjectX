using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Safety;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Cursor;

/// <summary>
/// Курсор с другой картинкой. Все системные курсоры (стрелка, текст, рука и другие) заменяются одной картинкой,
/// откат — схема курсоров пользователя, записанная в реестр изменений до замены.
/// </summary>
public abstract class CursorLookHardship : HardshipModule
{
    private IDisposable? _mark;

    /// <summary>Картинка курсора: пиксели, размер холста и горячая точка.</summary>
    protected abstract (uint[] Pixels, int Size, int HotX, int HotY) Picture(int systemSize);

    protected override void Begin()
    {
        _mark = Context.Changes.Record(CursorRestorer.ChangeKind, string.Empty);
        var (pixels, size, hotX, hotY) = Picture(CursorControl.SystemCursorSize());
        CursorControl.ReplaceCursors(pixels, size, hotX, hotY, allKinds: true);
    }

    protected override void End()
    {
        CursorControl.RestoreCursors();
        _mark?.Dispose();
        _mark = null;
    }
}

/// <summary>Гигантский курсор: стрелка в несколько раз больше обычной.</summary>
public sealed class GiantCursorHardship : CursorLookHardship
{
    public const int MaxSize = 256;

    public override HardshipInfo Info { get; } = new("giantCursor", HardshipLevel.Easy, HardshipResources.CursorLook);

    /// <summary>Размер холста и увеличение стрелки для обычного размера курсора системы.</summary>
    public static (int Size, int Scale) Measure(int systemSize)
    {
        var size = Math.Min(MaxSize, systemSize * 4);
        return (size, Math.Max(1, size / (CursorArt.ArrowHeight + 1)));
    }

    protected override (uint[] Pixels, int Size, int HotX, int HotY) Picture(int systemSize)
    {
        var (size, scale) = Measure(systemSize);
        var (pixels, hotX, hotY) = CursorArt.Arrow(size, scale);
        return (pixels, size, hotX, hotY);
    }
}

/// <summary>Крошечный курсор: стрелка в несколько пикселей.</summary>
public sealed class TinyCursorHardship : CursorLookHardship
{
    public override HardshipInfo Info { get; } = new("tinyCursor", HardshipLevel.Easy, HardshipResources.CursorLook);

    protected override (uint[] Pixels, int Size, int HotX, int HotY) Picture(int systemSize)
    {
        var (pixels, hotX, hotY) = CursorArt.Tiny(systemSize);
        return (pixels, systemSize, hotX, hotY);
    }
}

/// <summary>Перевёрнутый курсор: стрелка смотрит вниз и вправо, остриё по-прежнему точное.</summary>
public sealed class UpsideDownHardship : CursorLookHardship
{
    public override HardshipInfo Info { get; } = new("upsideDown", HardshipLevel.Easy, HardshipResources.CursorLook);

    protected override (uint[] Pixels, int Size, int HotX, int HotY) Picture(int systemSize)
    {
        var scale = Math.Max(1, systemSize / 32);
        var (pixels, hotX, hotY) = CursorArt.Arrow(systemSize, scale, flipped: true);
        return (pixels, systemSize, hotX, hotY);
    }
}

/// <summary>Мигающий курсор: стрелка то видна, то пропадает.</summary>
public sealed class BlinkHardship : HardshipModule
{
    public const double Shown = 0.8;
    public const double Hidden = 0.6;

    private readonly Lock _lock = new();
    private IDisposable? _mark;
    private Ticker? _ticker;
    private uint[] _blank = [];
    private int _size;
    private bool _hidden;
    private double _phase;

    public override HardshipInfo Info { get; } = new("blink", HardshipLevel.Easy, HardshipResources.CursorLook);

    protected override void Begin()
    {
        lock (_lock)
        {
            _mark = Context.Changes.Record(CursorRestorer.ChangeKind, string.Empty);
            _size = CursorControl.SystemCursorSize();
            _blank = CursorArt.Blank(_size);
            _hidden = false;
            _phase = 0;
        }

        _ticker = new Ticker("Blink", Step, error => Context.Log.Error("Ошибка мигающего курсора", error), intervalMs: 50);
    }

    protected override void End()
    {
        _ticker?.Dispose();
        _ticker = null;
        lock (_lock)
        {
            CursorControl.RestoreCursors();
            _mark?.Dispose();
            _mark = null;
        }
    }

    private void Step(double seconds)
    {
        lock (_lock)
        {
            _phase += seconds;
            if (_phase < (_hidden ? Hidden : Shown))
            {
                return;
            }

            _phase = 0;
            _hidden = !_hidden;
            if (_hidden)
            {
                CursorControl.ReplaceCursors(_blank, _size, 0, 0, allKinds: true);
            }
            else
            {
                CursorControl.RestoreCursors();
            }
        }
    }
}

/// <summary>
/// Смена кнопок: левая и правая кнопки мыши меняются местами. Клик всё равно приносит монетки
/// по физической кнопке: хук мыши получает <see cref="MouseButtonsSwapped"/> и переводит кнопки обратно.
/// </summary>
public sealed class SwapButtonsHardship : HardshipModule
{
    private IDisposable? _mark;
    private bool _original;

    public override HardshipInfo Info { get; } = new("swapButtons", HardshipLevel.Hard, HardshipResources.MouseButtons);

    protected override void Begin()
    {
        _original = MouseButtons.IsSwapped();
        _mark = Context.Changes.Record(MouseButtonsRestorer.ChangeKind, MouseButtonsRestorer.ToData(_original));
        MouseButtons.SetSwapped(!_original);
        Context.Events.Publish(new MouseButtonsSwapped(true));
    }

    protected override void End()
    {
        MouseButtons.SetSwapped(_original);
        Context.Events.Publish(new MouseButtonsSwapped(false));
        _mark?.Dispose();
        _mark = null;
    }
}

/// <summary>Возвращает назначение кнопок мыши, записанное до смены.</summary>
public sealed class MouseButtonsRestorer : ISystemRestorer
{
    public const string ChangeKind = "hardships.mouseButtons";

    public string Kind => ChangeKind;

    public static string ToData(bool swapped) => swapped ? "swapped" : "normal";

    public void Restore(string data) => MouseButtons.SetSwapped(data == "swapped");
}
