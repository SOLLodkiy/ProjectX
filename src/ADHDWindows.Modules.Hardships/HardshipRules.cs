using ADHDWindows.Core.Data;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Modules.Hardships;

/// <summary>Длительность одного уровня сложности: случайная между <paramref name="Min"/> и <paramref name="Max"/> секундами.</summary>
public sealed record LevelRules(double Min, double Max, decimal Bonus);

/// <summary>Режим: множитель дохода, во сколько раз чаще усложнения и веса уровней «лёгкое, среднее, сложное».</summary>
public sealed record ModeRules(decimal Multiplier, double Frequency, double[] Weights);

/// <summary>
/// Правила планировщика из data/hardships/rules.json. Если файла нет или он неверный, действуют значения из идеи 009.
/// </summary>
public sealed record HardshipRules(
    double IntervalMinutes,
    double SpreadMinutes,
    double WarningSeconds,
    double PostponeMinutes,
    double FirstDelayMinutes,
    IReadOnlyDictionary<HardshipLevel, LevelRules> Levels,
    IReadOnlyDictionary<HardshipMode, ModeRules> Modes)
{
    public const string DataKey = "hardships/rules";

    public static HardshipRules Default { get; } = new(
        IntervalMinutes: 20,
        SpreadMinutes: 5,
        WarningSeconds: 10,
        PostponeMinutes: 5,
        FirstDelayMinutes: 3,
        new Dictionary<HardshipLevel, LevelRules>
        {
            [HardshipLevel.Easy] = new(300, 300, 15),
            [HardshipLevel.Medium] = new(120, 180, 25),
            [HardshipLevel.Hard] = new(45, 60, 40),
        },
        new Dictionary<HardshipMode, ModeRules>
        {
            [HardshipMode.Off] = new(0.5m, 0, [0, 0, 0]),
            [HardshipMode.Soft] = new(0.75m, 1, [1, 0, 0]),
            [HardshipMode.Normal] = new(1m, 1, [3, 2, 1]),
            [HardshipMode.Hardcore] = new(1.5m, 1.6, [1, 2, 3]),
        });

    public ModeRules For(HardshipMode mode) => Modes.TryGetValue(mode, out var rules) ? rules : Default.Modes[mode];

    public LevelRules For(HardshipLevel level) => Levels.TryGetValue(level, out var rules) ? rules : Default.Levels[level];

    /// <summary>Правила из данных. Пропущенные значения берутся по умолчанию, неверные — тоже, с записью в журнал.</summary>
    public static HardshipRules Load(GameData data, ILog log)
    {
        if (!data.TryGet<HardshipRules>(DataKey, out var loaded) || loaded is null)
        {
            log.Warn($"Нет правил усложнений {DataKey}, действуют значения по умолчанию");
            return Default;
        }

        var levels = new Dictionary<HardshipLevel, LevelRules>(Default.Levels);
        foreach (var (level, rules) in loaded.Levels ?? new Dictionary<HardshipLevel, LevelRules>())
        {
            if (rules is { Min: > 0 } && rules.Max >= rules.Min && rules.Bonus >= 0)
            {
                levels[level] = rules;
            }
            else
            {
                log.Error($"Неверная длительность усложнений уровня {level}, действует значение по умолчанию");
            }
        }

        var modes = new Dictionary<HardshipMode, ModeRules>(Default.Modes);
        foreach (var (mode, rules) in loaded.Modes ?? new Dictionary<HardshipMode, ModeRules>())
        {
            if (rules is { Multiplier: > 0, Frequency: >= 0, Weights.Length: 3 } && rules.Weights.All(weight => weight >= 0))
            {
                modes[mode] = rules;
            }
            else
            {
                log.Error($"Неверные правила режима усложнений {mode}, действует значение по умолчанию");
            }
        }

        return new HardshipRules(
            Positive(loaded.IntervalMinutes, Default.IntervalMinutes),
            loaded.SpreadMinutes >= 0 ? loaded.SpreadMinutes : Default.SpreadMinutes,
            loaded.WarningSeconds >= 0 ? loaded.WarningSeconds : Default.WarningSeconds,
            Positive(loaded.PostponeMinutes, Default.PostponeMinutes),
            loaded.FirstDelayMinutes >= 0 ? loaded.FirstDelayMinutes : Default.FirstDelayMinutes,
            levels,
            modes);
    }

    private static double Positive(double value, double fallback) => value > 0 ? value : fallback;
}
