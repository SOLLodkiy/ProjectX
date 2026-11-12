using ADHDWindows.Core.Data;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.RandomEvents;

namespace ADHDWindows.Modules.Events;

/// <summary>
/// Шанс и длительность одного события. <paramref name="Weight"/> — вес при случайном выборе (0 — только вручную
/// или из очереди), <paramref name="Seconds"/> — сколько событие длится самое большее, <paramref name="PerDay"/> —
/// сколько раз за день оно может выпасть случайно (0 — без ограничения).
/// </summary>
public sealed record EventRule(double Weight, double Seconds, int PerDay);

/// <summary>Расписание событий из data/events/rules.json. Неверные значения заменяются значениями по умолчанию.</summary>
public sealed record EventRules(
    double IntervalMinutes,
    double SpreadMinutes,
    double FirstDelayMinutes,
    double PostponeMinutes,
    double HardshipGapSeconds,
    IReadOnlyDictionary<RandomEventFrequency, double> Frequencies,
    IReadOnlyDictionary<string, EventRule> Events)
{
    public const string DataKey = "events/rules";

    /// <summary>Правило события, которого нет в данных.</summary>
    public static EventRule DefaultRule { get; } = new(1, 60, 0);

    public static EventRules Default { get; } = new(
        IntervalMinutes: 22.5,
        SpreadMinutes: 7.5,
        FirstDelayMinutes: 4,
        PostponeMinutes: 3,
        HardshipGapSeconds: 90,
        new Dictionary<RandomEventFrequency, double>
        {
            [RandomEventFrequency.Off] = 0,
            [RandomEventFrequency.Rare] = 0.5,
            [RandomEventFrequency.Normal] = 1,
            [RandomEventFrequency.Often] = 2,
        },
        new Dictionary<string, EventRule>(StringComparer.Ordinal));

    public EventRule For(string id) => Events.TryGetValue(id, out var rule) ? rule : DefaultRule;

    public double FrequencyOf(RandomEventFrequency frequency) =>
        Frequencies.TryGetValue(frequency, out var value) ? value : Default.Frequencies[frequency];

    public static EventRules Load(GameData data, ILog log)
    {
        if (!data.TryGet<EventRules>(DataKey, out var loaded) || loaded is null)
        {
            log.Warn($"Нет расписания событий {DataKey}, действуют значения по умолчанию");
            return Default;
        }

        var result = loaded;
        if (loaded.IntervalMinutes <= 0 || loaded.SpreadMinutes < 0 || loaded.SpreadMinutes >= loaded.IntervalMinutes)
        {
            log.Error("Неверный интервал событий, действует значение по умолчанию");
            result = result with { IntervalMinutes = Default.IntervalMinutes, SpreadMinutes = Default.SpreadMinutes };
        }

        if (loaded.FirstDelayMinutes < 0 || loaded.PostponeMinutes <= 0 || loaded.HardshipGapSeconds < 0)
        {
            log.Error("Неверные паузы событий, действуют значения по умолчанию");
            result = result with
            {
                FirstDelayMinutes = Default.FirstDelayMinutes,
                PostponeMinutes = Default.PostponeMinutes,
                HardshipGapSeconds = Default.HardshipGapSeconds,
            };
        }

        var frequencies = new Dictionary<RandomEventFrequency, double>(Default.Frequencies);
        foreach (var (frequency, value) in loaded.Frequencies ?? new Dictionary<RandomEventFrequency, double>())
        {
            if (value >= 0)
            {
                frequencies[frequency] = value;
            }
            else
            {
                log.Error($"Неверная частота событий {frequency}, действует значение по умолчанию");
            }
        }

        var events = new Dictionary<string, EventRule>(StringComparer.Ordinal);
        foreach (var (id, rule) in loaded.Events ?? new Dictionary<string, EventRule>())
        {
            if (rule is { Weight: >= 0, Seconds: > 0, PerDay: >= 0 })
            {
                events[id] = rule;
            }
            else
            {
                log.Error($"Неверное правило события {id}, действует правило по умолчанию");
            }
        }

        return result with { Frequencies = frequencies, Events = events };
    }
}
