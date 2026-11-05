using ADHDWindows.Core.Input;

namespace ADHDWindows.Modules.Clicks;

public enum ClickVerdict
{
    /// <summary>Событие не является нажатием ЛКМ от устройства.</summary>
    NotAClick,

    /// <summary>Кнопка уже нажата: такое нажатие не считается новым.</summary>
    Held,

    Counted,

    /// <summary>Больше <see cref="ClickFilter.MaxClicksPerSecond"/> нажатий за секунду.</summary>
    TooFast,

    /// <summary>Серия слишком ровная для человека.</summary>
    TooEven,
}

/// <summary>
/// Решает, засчитать ли клик. Не потокобезопасен: события должны приходить из одного потока по порядку.
/// Время берётся только из событий, поэтому в тестах оно полностью управляемо.
/// </summary>
public sealed class ClickFilter
{
    public const int MaxClicksPerSecond = 20;

    /// <summary>Сколько промежутков подряд проверяется на ровность.</summary>
    public const int EvenSeriesIntervals = 12;

    /// <summary>
    /// Разброс промежутков, ниже которого серия считается автоматической. Даже у опытных барабанщиков
    /// разброс выше 8 мс, а программа и устройство укладываются в 1–3 мс.
    /// </summary>
    public const double EvenSeriesDeviationMs = 4;

    /// <summary>Сколько клики не приносят монет после ровной серии. Иначе одно случайное нажатие открывало бы 12 оплаченных.</summary>
    public static readonly TimeSpan EvenSeriesPenalty = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);

    private readonly Queue<TimeSpan> _lastSecond = new();
    private readonly Queue<TimeSpan> _series = new();
    private TimeSpan _penaltyEnds = TimeSpan.MinValue;
    private bool _held;

    public ClickVerdict Process(MouseButtonChanged change)
    {
        // Синтетические события не меняют состояние: программа не может ни нажать кнопку за игрока, ни отпустить её.
        if (change.Button != MouseButton.Left || change.Injected)
        {
            return ClickVerdict.NotAClick;
        }

        if (!change.Pressed)
        {
            _held = false;
            return ClickVerdict.NotAClick;
        }

        // Если отпускание потерялось, новое нажатие будет лишь пропущено, а следующее отпускание вернёт всё на место.
        if (_held)
        {
            return ClickVerdict.Held;
        }

        _held = true;
        Remember(change.Time);

        if (IsEvenSeries())
        {
            _penaltyEnds = change.Time + EvenSeriesPenalty;
        }

        if (change.Time < _penaltyEnds)
        {
            return ClickVerdict.TooEven;
        }

        return _lastSecond.Count > MaxClicksPerSecond ? ClickVerdict.TooFast : ClickVerdict.Counted;
    }

    private void Remember(TimeSpan time)
    {
        _lastSecond.Enqueue(time);
        while (time - _lastSecond.Peek() >= RateWindow)
        {
            _lastSecond.Dequeue();
        }

        _series.Enqueue(time);
        if (_series.Count > EvenSeriesIntervals + 1)
        {
            _series.Dequeue();
        }
    }

    private bool IsEvenSeries()
    {
        if (_series.Count <= EvenSeriesIntervals)
        {
            return false;
        }

        var intervals = _series.Zip(_series.Skip(1), (first, second) => (second - first).TotalMilliseconds).ToArray();
        var average = intervals.Average();
        var variance = intervals.Average(interval => (interval - average) * (interval - average));
        return Math.Sqrt(variance) <= EvenSeriesDeviationMs;
    }
}
