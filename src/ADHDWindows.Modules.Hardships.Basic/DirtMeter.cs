namespace ADHDWindows.Modules.Hardships.Basic;

/// <summary>
/// Грязь на курсоре для «Загрязнения»: растёт от движения и переходов между сайтами и окнами, снимается встряской —
/// несколькими быстрыми рывками из стороны в сторону. Потоками управляет владелец.
/// </summary>
public sealed class DirtMeter
{
    /// <summary>Грязь в начале усложнения: курсор сразу видно испачканным.</summary>
    public const double Initial = 0.2;

    /// <summary>Сколько пикселей движения пачкают курсор полностью.</summary>
    public const double FullDirtDistance = 40_000;

    public const double TransitionDirt = 0.08;

    /// <summary>Рывок засчитывается, если курсор прошёл в одну сторону хотя бы столько пикселей.</summary>
    public const double ShakeStroke = 60;

    /// <summary>Сколько смен направления за <see cref="ShakeWindow"/> секунд — встряска.</summary>
    public const int ShakeTurns = 4;

    public const double ShakeWindow = 1.2;

    public const double ShakeClean = 0.35;

    /// <summary>Во сколько раз падает скорость при полной грязи.</summary>
    public const double MaxSlowdown = 0.7;

    private readonly Queue<double> _turns = new();
    private double _time;
    private int _direction;
    private double _stroke;

    public double Level { get; private set; } = Initial;

    /// <summary>Сколько раз курсор отряхнули.</summary>
    public int Shakes { get; private set; }

    /// <summary>Курсор сдвинулся на <paramref name="dx"/>, <paramref name="dy"/> за <paramref name="seconds"/>.</summary>
    public void Move(double dx, double dy, double seconds)
    {
        _time += seconds;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        Add(distance / FullDirtDistance);

        var direction = Math.Sign(dx);
        if (direction == 0)
        {
            return;
        }

        if (direction == _direction)
        {
            _stroke += Math.Abs(dx);
            return;
        }

        // Смена направления считается, только если предыдущий рывок был достаточно длинным.
        if (_direction != 0 && _stroke >= ShakeStroke)
        {
            _turns.Enqueue(_time);
        }

        _direction = direction;
        _stroke = Math.Abs(dx);
        while (_turns.Count > 0 && _time - _turns.Peek() > ShakeWindow)
        {
            _turns.Dequeue();
        }

        if (_turns.Count >= ShakeTurns)
        {
            _turns.Clear();
            Level = Math.Max(0, Level - ShakeClean);
            Shakes++;
        }
    }

    /// <summary>Переход на другой сайт или в другое окно.</summary>
    public void Transition() => Add(TransitionDirt);

    /// <summary>Скорость указателя от 1 до 20 при текущей грязи.</summary>
    public int SpeedFor(int original) =>
        Math.Clamp((int)Math.Round(original * (1 - (MaxSlowdown * Level))), 1, Math.Max(1, original));

    /// <summary>Ступень грязи для картинки курсора: картинка меняется только при смене ступени.</summary>
    public int Step(int steps = 8) => (int)Math.Round(Level * steps);

    private void Add(double amount) => Level = Math.Min(1, Level + amount);
}
