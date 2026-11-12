using System.Text.Json;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Modules.Progress;

/// <summary>
/// Звание: действует с уровня <see cref="Level"/>. <see cref="Coins"/> — прибавка к награде за уровень, на котором дают звание,
/// <see cref="Cosmetic"/> — код косметики, которую звание открывает (идея 012).
/// </summary>
public sealed record RankDefinition(string Id, int Level, string Name, long Coins, string? Cosmetic);

/// <summary>Опыт за счётчик: каждые <see cref="Per"/> единиц счётчика <see cref="Metric"/> дают <see cref="Xp"/> опыта.</summary>
public sealed record XpRule(string Metric, double Per, long Xp);

/// <summary>
/// Достижение из данных. Цель — счётчик <see cref="Metric"/> не меньше <see cref="Goal"/>.
/// Если заданы часы <see cref="FromHour"/>–<see cref="ToHour"/> (местное время, конец не входит, через полночь можно),
/// засчитывается только то, что набрано в эти часы, начиная с появления достижения в данных.
/// </summary>
public sealed record AchievementDefinition(
    string Id,
    string Metric,
    double Goal,
    long Reward,
    long Xp,
    string Icon,
    string Title,
    string Text,
    bool Hidden,
    int? FromHour,
    int? ToHour)
{
    public bool HasHours => FromHour is not null && ToHour is not null;

    public bool InHours(int hour) => FromHour is not { } from || ToHour is not { } to
        || (from <= to ? hour >= from && hour < to : hour >= from || hour < to);
}

/// <summary>Шаблон цели недели. Прогресс считается от значения счётчика на момент выдачи цели.</summary>
public sealed record WeeklyGoalDefinition(string Id, string Metric, double Goal, long Reward, long Xp, string Icon, string Title);

/// <summary>Награда за ежедневный вход: <see cref="Base"/> в первый день, плюс <see cref="PerDay"/> за каждый следующий, не больше <see cref="Max"/>.</summary>
public sealed record StreakRules(long Base, long PerDay, long Max, bool FreeSkip)
{
    public static readonly StreakRules Default = new(20, 10, 300, true);

    public long Reward(int days) => Math.Min(Max, Base + (PerDay * Math.Max(0, days - 1)));
}

/// <summary>
/// Опыт до следующего уровня: <see cref="First"/> с первого на второй, и на <see cref="Step"/> больше на каждом следующем.
/// Награда за уровень L: <see cref="CoinsBase"/> + <see cref="CoinsPerLevel"/> × (L − 2), плюс прибавка звания.
/// </summary>
public sealed record LevelRules(long First, long Step, long CoinsBase, long CoinsPerLevel, int MaxLevel)
{
    public static readonly LevelRules Default = new(100, 50, 50, 20, 100);

    /// <summary>Сколько опыта нужно, чтобы с уровня <paramref name="level"/> перейти на следующий.</summary>
    public long Need(int level) => First + (Step * Math.Max(0, level - 1));

    /// <summary>Уровень и опыт внутри него при общем опыте <paramref name="xp"/>. На последнем уровне остаток не обнуляется.</summary>
    public (int Level, long Into, long Need) Level(long xp)
    {
        var level = 1;
        while (level < MaxLevel && xp >= Need(level))
        {
            xp -= Need(level);
            level++;
        }

        return (level, xp, level < MaxLevel ? Need(level) : 0);
    }

    public long Coins(int level) => CoinsBase + (CoinsPerLevel * Math.Max(0, level - 2));
}

/// <summary>Весь контент прогресса из data/progress.</summary>
public sealed record ProgressCatalog(
    LevelRules Levels,
    IReadOnlyList<RankDefinition> Ranks,
    IReadOnlyList<XpRule> XpRules,
    IReadOnlyList<AchievementDefinition> Achievements,
    StreakRules Streak,
    int WeeklyCount,
    IReadOnlyList<WeeklyGoalDefinition> WeeklyGoals)
{
    /// <summary>Файл уровней, званий и правил опыта.</summary>
    public const string LevelsKey = "progress/levels";

    public const string StreakKey = "progress/streak";

    /// <summary>Достижения читаются из всех файлов data/progress/achievements*.json.</summary>
    public const string AchievementsPrefix = "progress/achievements";

    /// <summary>Цели недели читаются из всех файлов data/progress/weekly*.json.</summary>
    public const string WeeklyPrefix = "progress/weekly";

    private const string DefaultIcon = "trophy";
    private const int DefaultWeeklyCount = 3;

    public RankDefinition RankAt(int level) =>
        Ranks.LastOrDefault(rank => rank.Level <= level) ?? new RankDefinition("novice", 1, "Новичок", 0, null);

    /// <summary>Читает данные. Ошибочные записи пропускаются с записью в журнал и не мешают остальным.</summary>
    public static ProgressCatalog Load(GameData data, ILog log)
    {
        var levels = LevelRules.Default;
        var ranks = new List<RankDefinition>();
        var rules = new List<XpRule>();
        if (data.TryGet<JsonElement>(LevelsKey, out var levelsFile) && levelsFile.ValueKind == JsonValueKind.Object)
        {
            levels = ReadLevels(levelsFile, log);
            ranks = ReadRanks(levelsFile, log);
            rules = ReadXpRules(levelsFile, log);
        }
        else
        {
            log.Warn($"Нет данных {LevelsKey}, уровни по умолчанию");
        }

        var streak = StreakRules.Default;
        if (data.TryGet<JsonElement>(StreakKey, out var streakFile) && streakFile.ValueKind == JsonValueKind.Object)
        {
            streak = new StreakRules(
                Long(streakFile, "base") ?? streak.Base,
                Long(streakFile, "perDay") ?? streak.PerDay,
                Long(streakFile, "max") ?? streak.Max,
                streakFile.TryGetProperty("freeSkip", out var skip) ? skip.ValueKind != JsonValueKind.False : streak.FreeSkip);
            if (streak.Base < 0 || streak.PerDay < 0 || streak.Max < streak.Base)
            {
                log.Error($"Неверные награды серии в {StreakKey}, взяты по умолчанию");
                streak = StreakRules.Default;
            }
        }

        var achievements = new List<AchievementDefinition>();
        var weekly = new List<WeeklyGoalDefinition>();
        var weeklyCount = DefaultWeeklyCount;
        foreach (var key in data.Keys.Order(StringComparer.Ordinal))
        {
            if (key.StartsWith(AchievementsPrefix, StringComparison.OrdinalIgnoreCase) && data.TryGet<JsonElement>(key, out var file))
            {
                achievements.AddRange(ReadAchievements(file, key, achievements, log));
            }
            else if (key.StartsWith(WeeklyPrefix, StringComparison.OrdinalIgnoreCase) && data.TryGet<JsonElement>(key, out var weeklyFile))
            {
                if (Long(weeklyFile, "count") is { } count and > 0 and < 10)
                {
                    weeklyCount = (int)count;
                }

                weekly.AddRange(ReadWeekly(weeklyFile, key, weekly, log));
            }
        }

        return new ProgressCatalog(levels, ranks, rules, achievements, streak, weeklyCount, weekly);
    }

    /// <summary>Разбирает достижения одного файла. Для проверок и пула режима разработчика.</summary>
    internal static List<AchievementDefinition> ReadAchievements(JsonElement file, string source, IReadOnlyList<AchievementDefinition> known, ILog log)
    {
        var result = new List<AchievementDefinition>();
        if (!file.TryGetProperty("achievements", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            log.Error($"В {source} нет списка achievements");
            return result;
        }

        foreach (var item in list.EnumerateArray())
        {
            var id = Text(item, "id");
            var metric = Text(item, "metric");
            var title = Text(item, "title");
            var goal = Number(item, "goal") ?? 0;
            var reward = Long(item, "reward") ?? 0;
            int? from = null;
            int? to = null;
            var hoursValid = true;
            if (item.TryGetProperty("hours", out var hours))
            {
                var pair = hours.ValueKind == JsonValueKind.Array ? hours.EnumerateArray().Select(hour => hour.TryGetInt32(out var value) ? value : -1).ToList() : [];
                hoursValid = pair.Count == 2 && pair.All(hour => hour is >= 0 and <= 24) && pair[0] != pair[1];
                if (hoursValid)
                {
                    from = pair[0] % 24;
                    to = pair[1] % 24;
                }
            }

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(metric) || string.IsNullOrWhiteSpace(title))
            {
                log.Error($"Достижение без кода, счётчика или названия пропущено: {source}");
                continue;
            }

            if (known.Any(other => other.Id == id) || result.Any(other => other.Id == id))
            {
                log.Error($"Повтор кода достижения {id} пропущен: {source}");
                continue;
            }

            if (goal <= 0 || reward < 0 || !hoursValid)
            {
                log.Error($"Достижение {id} с неверной целью, наградой или часами пропущено: {source}");
                continue;
            }

            if (!ProgressMetrics.IsKnown(metric))
            {
                log.Warn($"Достижение {id}: счётчик {metric} пока никто не считает");
            }

            result.Add(new AchievementDefinition(
                id,
                metric,
                goal,
                reward,
                Long(item, "xp") ?? DefaultXp(reward),
                Text(item, "icon") ?? DefaultIcon,
                title,
                Text(item, "text") ?? string.Empty,
                item.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True,
                from,
                to));
        }

        return result;
    }

    private static List<WeeklyGoalDefinition> ReadWeekly(JsonElement file, string source, IReadOnlyList<WeeklyGoalDefinition> known, ILog log)
    {
        var result = new List<WeeklyGoalDefinition>();
        if (!file.TryGetProperty("goals", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            log.Error($"В {source} нет списка goals");
            return result;
        }

        foreach (var item in list.EnumerateArray())
        {
            var id = Text(item, "id");
            var metric = Text(item, "metric");
            var title = Text(item, "title");
            var goal = Number(item, "goal") ?? 0;
            var reward = Long(item, "reward") ?? 0;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(metric) || string.IsNullOrWhiteSpace(title) || goal <= 0 || reward < 0)
            {
                log.Error($"Неверная цель недели пропущена: {source}, {id}");
                continue;
            }

            if (known.Any(other => other.Id == id) || result.Any(other => other.Id == id))
            {
                log.Error($"Повтор кода цели недели {id} пропущен: {source}");
                continue;
            }

            if (ProgressMetrics.IsGauge(metric))
            {
                // Цель недели считается приростом счётчика, а уровень или серия за неделю не «прирастают» так, как ждёт игрок.
                log.Error($"Цель недели {id} не может считать {metric}: {source}");
                continue;
            }

            if (!ProgressMetrics.IsKnown(metric))
            {
                log.Warn($"Цель недели {id}: счётчик {metric} пока никто не считает");
            }

            result.Add(new WeeklyGoalDefinition(id, metric, goal, reward, Long(item, "xp") ?? DefaultXp(reward), Text(item, "icon") ?? DefaultIcon, title));
        }

        return result;
    }

    private static LevelRules ReadLevels(JsonElement file, ILog log)
    {
        var rules = LevelRules.Default;
        if (file.TryGetProperty("xp", out var xp))
        {
            rules = rules with { First = Long(xp, "first") ?? rules.First, Step = Long(xp, "step") ?? rules.Step };
        }

        if (file.TryGetProperty("coins", out var coins))
        {
            rules = rules with { CoinsBase = Long(coins, "base") ?? rules.CoinsBase, CoinsPerLevel = Long(coins, "perLevel") ?? rules.CoinsPerLevel };
        }

        rules = rules with { MaxLevel = (int)(Long(file, "maxLevel") ?? rules.MaxLevel) };
        if (rules.First <= 0 || rules.Step < 0 || rules.CoinsBase < 0 || rules.CoinsPerLevel < 0 || rules.MaxLevel is < 2 or > 10_000)
        {
            log.Error($"Неверные правила уровней в {LevelsKey}, взяты по умолчанию");
            return LevelRules.Default;
        }

        return rules;
    }

    private static List<RankDefinition> ReadRanks(JsonElement file, ILog log)
    {
        var ranks = new List<RankDefinition>();
        if (!file.TryGetProperty("ranks", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            log.Error($"В {LevelsKey} нет званий");
            return ranks;
        }

        foreach (var item in list.EnumerateArray())
        {
            var id = Text(item, "id");
            var name = Text(item, "name");
            var level = Long(item, "level") ?? 0;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || level < 1 || ranks.Any(rank => rank.Id == id || rank.Level == level))
            {
                log.Error($"Неверное звание пропущено: {id}");
                continue;
            }

            ranks.Add(new RankDefinition(id, (int)level, name, Long(item, "coins") ?? 0, Text(item, "cosmetic")));
        }

        ranks.Sort((left, right) => left.Level.CompareTo(right.Level));
        if (ranks.Count > 0 && ranks[0].Level != 1)
        {
            log.Error("Первое звание должно начинаться с уровня 1");
        }

        return ranks;
    }

    private static List<XpRule> ReadXpRules(JsonElement file, ILog log)
    {
        var rules = new List<XpRule>();
        if (!file.TryGetProperty("xpRules", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            log.Error($"В {LevelsKey} нет правил опыта xpRules");
            return rules;
        }

        foreach (var item in list.EnumerateArray())
        {
            var metric = Text(item, "metric");
            var per = Number(item, "per") ?? 0;
            var xp = Long(item, "xp") ?? 0;
            if (string.IsNullOrWhiteSpace(metric) || per <= 0 || xp <= 0 || ProgressMetrics.IsGauge(metric))
            {
                log.Error($"Неверное правило опыта пропущено: {metric}");
                continue;
            }

            if (!ProgressMetrics.IsKnown(metric))
            {
                log.Warn($"Правило опыта: счётчик {metric} пока никто не считает");
            }

            rules.Add(new XpRule(metric, per, xp));
        }

        return rules;
    }

    /// <summary>Опыт за достижение или цель, если в данных он не указан: пятая часть награды, но не меньше 10.</summary>
    private static long DefaultXp(long reward) => Math.Max(10, reward / 5);

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static long? Long(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}

/// <summary>
/// Счётчики, по которым считаются опыт, достижения и цели недели. Счётчик — сумма за всё время,
/// показатель (<see cref="IsGauge"/>) — текущее значение: уровень, серия, число достижений.
/// Любой модуль может завести свой счётчик событием <c>StatCounted</c>: его код становится счётчиком прогресса.
/// </summary>
public static class ProgressMetrics
{
    public const string Clicks = "clicks";
    public const string Earned = "earned";
    public const string Spent = "spent";
    public const string MediaSeconds = "media.seconds";
    public const string Unlocks = "unlocks";
    public const string Upgrades = "upgrades";
    public const string QuestsCompleted = "quests.completed";
    public const string HardshipsSurvived = "hardships.survived";
    public const string HardshipsStarted = "hardships.started";
    public const string ProgramsSeen = "programs.distinct";
    public const string DaysActive = "days.active";
    public const string Launches = "app.launches";
    public const string StreakRewards = "streak.rewards";
    public const string WeeklyCompleted = "weekly.completed";

    public const string Level = "level";
    public const string Streak = "streak";
    public const string BestStreak = "streak.best";
    public const string Achievements = "achievements";

    /// <summary>Префикс счётчика программы: <c>program.notepad</c> растёт, когда новый процесс notepad.exe становится активным окном.</summary>
    public const string ProgramPrefix = "program.";

    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal)
    {
        Clicks, Earned, Spent, MediaSeconds, Unlocks, Upgrades, QuestsCompleted, HardshipsSurvived, HardshipsStarted,
        ProgramsSeen, DaysActive, Launches, StreakRewards, WeeklyCompleted, "quests.chains", "quests.coins",
    };

    private static readonly HashSet<string> Gauges = new(StringComparer.Ordinal) { Level, Streak, BestStreak, Achievements };

    private static readonly string[] Prefixes =
    [
        "clicks.", "earned.", "media.", "unlocks.", "quests.completed.", "hardships.survived.", ProgramPrefix,
    ];

    public static bool IsGauge(string metric) => Gauges.Contains(metric);

    public static bool IsKnown(string metric) =>
        Exact.Contains(metric) || Gauges.Contains(metric) || Prefixes.Any(prefix => metric.StartsWith(prefix, StringComparison.Ordinal) && metric.Length > prefix.Length);
}
