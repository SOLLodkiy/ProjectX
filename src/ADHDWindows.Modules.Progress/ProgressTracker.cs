using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Progress;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.Stats;
using ADHDWindows.Core.Storage;
using ADHDWindows.Core.Upgrades;

namespace ADHDWindows.Modules.Progress;

/// <summary>Итоги текущего дня, пока он идёт. Дата местная.</summary>
public sealed class DayRecord
{
    public DateOnly Date { get; set; }
    public long Earned { get; set; }
    public long Spent { get; set; }
    public long Clicks { get; set; }
    public double MediaSeconds { get; set; }
    public long XpStart { get; set; }
    public int Achievements { get; set; }
    public int Levels { get; set; }
    public Dictionary<string, long> BySource { get; set; } = [];
}

/// <summary>Выданная цель недели. <see cref="Baseline"/> — значение счётчика в момент выдачи.</summary>
public sealed class WeeklyGoalState
{
    public string Id { get; set; } = string.Empty;
    public double Baseline { get; set; }
    public bool Completed { get; set; }
}

/// <summary>Прогресс в сохранении.</summary>
public sealed class ProgressState
{
    public Dictionary<string, double> Counters { get; set; } = [];

    /// <summary>До этого уровня награды уже выданы. Уровень считается из опыта, награда — один раз.</summary>
    public int RewardedLevel { get; set; } = 1;

    /// <summary>Опыт за достижения и цели недели. Остальной опыт считается из счётчиков.</summary>
    public long BonusXp { get; set; }

    /// <summary>Полученные достижения и когда.</summary>
    public Dictionary<string, DateTimeOffset> Achievements { get; set; } = [];

    /// <summary>Набранное в нужные часы для достижений с часами.</summary>
    public Dictionary<string, double> HourProgress { get; set; } = [];

    public int Streak { get; set; }
    public int BestStreak { get; set; }

    /// <summary>Последний день, за который засчитан вход.</summary>
    public DateOnly? LastLoginDay { get; set; }

    /// <summary>Неделя (её понедельник), в которой уже закрыт пропуск дня.</summary>
    public DateOnly? SkipWeek { get; set; }

    public DateOnly? WeekStart { get; set; }
    public List<WeeklyGoalState> Weekly { get; set; } = [];

    public DayRecord? Day { get; set; }
    public DaySummary? LastSummary { get; set; }
    public long BestDayEarned { get; set; }

    /// <summary>Программы, которые хоть раз были в активном окне: по ним считается число разных программ.</summary>
    public List<string> SeenPrograms { get; set; } = [];
}

/// <summary>
/// Опыт, уровни, достижения, ежедневная серия, итог дня и цели недели. Методы можно вызывать из любого потока.
/// Награды проходят через общую формулу дохода и зачисляются вне блокировки, после изменения состояния.
/// Дни и недели местные: день кончается в полночь игрока, неделя начинается в понедельник.
/// </summary>
public sealed class ProgressTracker
{
    public const string SaveKey = "state";

    /// <summary>Источники дохода прогресса. Их монетки не считаются в счётчик <c>earned</c>, чтобы награды не давали опыт по кругу.</summary>
    public const string LevelSource = "level";

    public const string AchievementSource = "achievement";
    public const string StreakSource = "streak";
    public const string WeeklySource = "weekly";

    /// <summary>Столько разных программ запоминается.</summary>
    public const int ProgramLimit = 2000;

    private static readonly HashSet<string> OwnSources = new(StringComparer.Ordinal) { LevelSource, AchievementSource, StreakSource, WeeklySource };

    private readonly ProgressCatalog _catalog;
    private readonly IEconomy _economy;
    private readonly IEventBus _events;
    private readonly IKeyValueStore _saves;
    private readonly IKeyValueStore _settings;
    private readonly TimeProvider _clock;
    private readonly Random _random;
    private readonly ILog _log;
    private readonly Lock _lock = new();
    private readonly ProgressState _state;
    private readonly Dictionary<string, List<AchievementDefinition>> _hourly;

    public ProgressTracker(
        ProgressCatalog catalog,
        IEconomy economy,
        IEventBus events,
        IKeyValueStore saves,
        IKeyValueStore settings,
        TimeProvider clock,
        Random random,
        ILog log)
    {
        _catalog = catalog;
        _economy = economy;
        _events = events;
        _saves = saves;
        _settings = settings;
        _clock = clock;
        _random = random;
        _log = log;
        _state = saves.Get<ProgressState>(SaveKey) ?? new ProgressState();
        _hourly = catalog.Achievements.Where(achievement => achievement.HasHours)
            .GroupBy(achievement => achievement.Metric, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
    }

    public bool Notifications => _settings.Get(ProgressSettings.Notifications, true);

    public bool ShowDaySummary => _settings.Get(ProgressSettings.DaySummary, true);

    /// <summary>Запуск программы: засчитывает вход дня и выдаёт цели недели.</summary>
    public void Start() => Change(state => Add(state, ProgressMetrics.Launches, 1), notable: true);

    /// <summary>Раз в несколько секунд: смена дня и недели, пока программа открыта.</summary>
    public void Tick() => Change(_ => { });

    public void OnClick(ClickCounted click) => Change(state =>
    {
        Add(state, ProgressMetrics.Clicks, 1);
        Add(state, $"{ProgressMetrics.Clicks}.{click.Button.ToString().ToLowerInvariant()}", 1);
        state.Day!.Clicks++;
    });

    public void OnEarned(CoinsEarned earned)
    {
        if (earned.Amount <= 0)
        {
            return;
        }

        Change(state =>
        {
            var day = state.Day!;
            day.Earned = Sum(day.Earned, earned.Amount);
            day.BySource[earned.Source] = Sum(day.BySource.GetValueOrDefault(earned.Source), earned.Amount);
            if (!OwnSources.Contains(earned.Source))
            {
                Add(state, ProgressMetrics.Earned, earned.Amount);
                Add(state, $"{ProgressMetrics.Earned}.{earned.Source}", earned.Amount);
            }
        });
    }

    public void OnSpent(CoinsSpent spent)
    {
        if (spent.Amount <= 0)
        {
            return;
        }

        Change(state =>
        {
            Add(state, ProgressMetrics.Spent, spent.Amount);
            state.Day!.Spent = Sum(state.Day.Spent, spent.Amount);
        });
    }

    public void OnMedia(MediaTimeCounted counted)
    {
        if (counted.Seconds <= 0 || !double.IsFinite(counted.Seconds))
        {
            return;
        }

        Change(state =>
        {
            Add(state, ProgressMetrics.MediaSeconds, counted.Seconds);
            Add(state, $"media.{counted.Source}.seconds", counted.Seconds);
            state.Day!.MediaSeconds += counted.Seconds;
        });
    }

    public void OnUnlock(ItemUnlocked unlocked) => Change(state =>
    {
        Add(state, ProgressMetrics.Unlocks, 1);
        Add(state, $"{ProgressMetrics.Unlocks}.{unlocked.Offer.Kind.ToString().ToLowerInvariant()}", 1);
    });

    public void OnUpgrade(UpgradePurchased purchased) => Change(state => Add(state, ProgressMetrics.Upgrades, 1));

    public void OnQuest(QuestCompleted completed) => Change(state => Add(state, ProgressMetrics.QuestsCompleted, 1));

    /// <summary>Счётчик любой механики становится счётчиком прогресса с тем же кодом.</summary>
    public void OnCounter(StatCounted counted)
    {
        if (!string.IsNullOrWhiteSpace(counted.Key) && counted.Amount > 0)
        {
            Change(state => Add(state, counted.Key, counted.Amount));
        }
    }

    public void OnHardshipStarted(HardshipStarted started) => Change(state => Add(state, ProgressMetrics.HardshipsStarted, 1));

    public void OnHardshipEnded(HardshipEnded ended)
    {
        if (ended.Reason != HardshipEndReason.Survived)
        {
            return;
        }

        Change(state =>
        {
            Add(state, ProgressMetrics.HardshipsSurvived, 1);
            Add(state, $"{ProgressMetrics.HardshipsSurvived}.{ended.Hardship.Level.ToString().ToLowerInvariant()}", 1);
        });
    }

    /// <summary>Новый процесс программы стал активным окном. <paramref name="name"/> — имя файла без расширения.</summary>
    public void OnProgram(string name)
    {
        name = name.Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            return;
        }

        Change(state =>
        {
            Add(state, ProgressMetrics.ProgramPrefix + name, 1);
            if (!state.SeenPrograms.Contains(name) && state.SeenPrograms.Count < ProgramLimit)
            {
                state.SeenPrograms.Add(name);
                Add(state, ProgressMetrics.ProgramsSeen, 1);
            }
        });
    }

    public void SetSetting(string key, bool value)
    {
        if (key is ProgressSettings.Notifications or ProgressSettings.DaySummary)
        {
            _settings.Set(key, value);
            Publish();
        }
    }

    public void Publish() => _events.Publish(new ProgressChanged(Snapshot()));

    public void Save()
    {
        lock (_lock)
        {
            _saves.Set(SaveKey, _state);
        }
    }

    public ProgressSnapshot Snapshot()
    {
        lock (_lock)
        {
            var state = _state;
            var xp = TotalXp(state);
            var (level, into, need) = _catalog.Levels.Level(xp);
            var rank = _catalog.RankAt(level);
            var today = Today();
            return new ProgressSnapshot(
                level,
                rank.Id,
                rank.Name,
                xp,
                into,
                need,
                state.Streak,
                state.BestStreak,
                _catalog.Streak.FreeSkip && state.SkipWeek != StatsSeries.WeekStart(today),
                _catalog.Streak.Reward(state.Streak + 1),
                [.. _catalog.Achievements.Select(achievement => View(state, achievement))],
                [.. Weekly(state)],
                state.WeekStart ?? StatsSeries.WeekStart(today),
                state.Day is { } day ? Summarize(state, day) : new DaySummary(today, 0, 0, 0, 0, 0, 0, 0, null, 0, false, state.Streak),
                state.LastSummary,
                Notifications,
                ShowDaySummary);
        }
    }

    /// <summary>Значение счётчика или показателя.</summary>
    internal double Value(string metric)
    {
        lock (_lock)
        {
            return Value(_state, metric);
        }
    }

    private void Change(Action<ProgressState> change, bool notable = false)
    {
        var rewards = new List<Reward>();
        var events = new List<DaySummaryReady>();
        lock (_lock)
        {
            Roll(_state, rewards, events);
            change(_state);
            Evaluate(_state, rewards);
        }

        Flush(rewards, events, notable);
    }

    /// <summary>Зачисляет награды через формулу дохода и рассылает события. Вызывается вне блокировки: зачисление вернётся сюда же событием дохода.</summary>
    private void Flush(List<Reward> rewards, List<DaySummaryReady> events, bool notable)
    {
        foreach (var reward in rewards)
        {
            var earned = reward.Amount > 0 ? _economy.Earn(reward.Source, reward.Amount) : 0;
            reward.Announce(earned);
        }

        foreach (var @event in events)
        {
            _events.Publish(@event);
        }

        if (notable || rewards.Count > 0 || events.Count > 0)
        {
            Save();
            Publish();
        }
    }

    /// <summary>Новый день и новая неделя: итог прошедшего дня, награда серии, новые цели.</summary>
    private void Roll(ProgressState state, List<Reward> rewards, List<DaySummaryReady> events)
    {
        var today = Today();

        // Неделя раньше дня: базовое значение целей недели берётся до того, как засчитан понедельник.
        RollWeek(state, today);
        if (state.Day is not { } day)
        {
            state.Day = new DayRecord { Date = today, XpStart = TotalXp(state) };
            Add(state, ProgressMetrics.DaysActive, 1);
        }
        else if (today > day.Date)
        {
            var summary = Summarize(state, day);
            state.LastSummary = summary;
            state.BestDayEarned = Math.Max(state.BestDayEarned, day.Earned);
            if (day.Clicks > 0 || day.MediaSeconds > 0 || day.BySource.Any(pair => !OwnSources.Contains(pair.Key) && pair.Value > 0))
            {
                events.Add(new DaySummaryReady(summary, ShowDaySummary));
            }

            state.Day = new DayRecord { Date = today, XpStart = TotalXp(state) };
            Add(state, ProgressMetrics.DaysActive, 1);
        }

        // Награда серии сама по себе не делает день активным: пустой день итога не даёт.
        // Часы переведены назад: прежний день продолжается, серия не растёт и не рвётся.
        RollStreak(state, today, rewards);
    }

    private void RollStreak(ProgressState state, DateOnly today, List<Reward> rewards)
    {
        if (state.LastLoginDay is { } last && today <= last)
        {
            return;
        }

        var skipUsed = false;
        var broken = false;
        if (state.LastLoginDay is not { } previous)
        {
            state.Streak = 1;
        }
        else
        {
            var gap = today.DayNumber - previous.DayNumber;
            var missed = previous.AddDays(1);
            if (gap == 1)
            {
                state.Streak++;
            }
            else if (gap == 2 && _catalog.Streak.FreeSkip && state.SkipWeek != StatsSeries.WeekStart(missed))
            {
                // Один пропущенный день в неделю прощается: серия продолжается, сам пропуск днём серии не считается.
                state.SkipWeek = StatsSeries.WeekStart(missed);
                state.Streak++;
                skipUsed = true;
            }
            else
            {
                broken = state.Streak > 1;
                state.Streak = 1;
            }
        }

        state.LastLoginDay = today;
        state.BestStreak = Math.Max(state.BestStreak, state.Streak);
        Add(state, ProgressMetrics.StreakRewards, 1);
        var days = state.Streak;
        var notify = Notifications;
        rewards.Add(new Reward(StreakSource, _catalog.Streak.Reward(days), earned => _events.Publish(new StreakRewarded(days, earned, skipUsed, broken, notify))));
    }

    private void RollWeek(ProgressState state, DateOnly today)
    {
        var week = StatsSeries.WeekStart(today);
        if (state.WeekStart is { } current && week <= current)
        {
            return;
        }

        state.WeekStart = week;
        state.Weekly = [.. _catalog.WeeklyGoals
            .OrderBy(_ => _random.Next())
            .Take(_catalog.WeeklyCount)
            .Select(goal => new WeeklyGoalState { Id = goal.Id, Baseline = Value(state, goal.Metric) })];
        _log.Info($"Цели недели с {week:yyyy-MM-dd}: {string.Join(", ", state.Weekly.Select(goal => goal.Id))}");
    }

    /// <summary>Проверяет достижения, цели недели и уровень, пока что-то меняется: новое достижение может дать уровень, уровень — достижение.</summary>
    private void Evaluate(ProgressState state, List<Reward> rewards)
    {
        var notify = Notifications;
        bool changed;
        var guard = 0;
        do
        {
            changed = false;
            foreach (var achievement in _catalog.Achievements)
            {
                if (state.Achievements.ContainsKey(achievement.Id) || Progress(state, achievement) < achievement.Goal)
                {
                    continue;
                }

                state.Achievements[achievement.Id] = _clock.GetUtcNow();
                state.BonusXp = Sum(state.BonusXp, achievement.Xp);
                state.Day!.Achievements++;
                var view = View(state, achievement);
                rewards.Add(new Reward(AchievementSource, achievement.Reward, earned => _events.Publish(new AchievementUnlocked(view, earned, notify))));
                changed = true;
            }

            foreach (var goal in state.Weekly.Where(goal => !goal.Completed))
            {
                if (_catalog.WeeklyGoals.FirstOrDefault(definition => definition.Id == goal.Id) is not { } definition
                    || Value(state, definition.Metric) - goal.Baseline < definition.Goal)
                {
                    continue;
                }

                goal.Completed = true;
                state.BonusXp = Sum(state.BonusXp, definition.Xp);
                Add(state, ProgressMetrics.WeeklyCompleted, 1);
                var view = WeeklyView(state, definition, goal);
                rewards.Add(new Reward(WeeklySource, definition.Reward, earned => _events.Publish(new WeeklyGoalCompleted(view, earned, notify))));
                changed = true;
            }

            var level = _catalog.Levels.Level(TotalXp(state)).Level;
            while (state.RewardedLevel < level)
            {
                var reached = ++state.RewardedLevel;
                var rank = _catalog.RankAt(reached);
                var previous = _catalog.RankAt(reached - 1);
                var rankChanged = rank.Id != previous.Id && rank.Level == reached;
                var coins = _catalog.Levels.Coins(reached) + (rankChanged ? rank.Coins : 0);
                state.Day!.Levels++;
                rewards.Add(new Reward(LevelSource, coins, earned => _events.Publish(new LevelReached(reached, rank.Id, rank.Name, rankChanged, earned, rankChanged ? rank.Cosmetic : null, notify))));
                changed = true;
            }
        }
        while (changed && ++guard < 100);
    }

    private void Add(ProgressState state, string metric, double amount)
    {
        state.Counters[metric] = state.Counters.GetValueOrDefault(metric) + amount;
        if (_hourly.TryGetValue(metric, out var hourly))
        {
            var hour = _clock.GetLocalNow().Hour;
            foreach (var achievement in hourly.Where(achievement => achievement.InHours(hour) && !state.Achievements.ContainsKey(achievement.Id)))
            {
                state.HourProgress[achievement.Id] = state.HourProgress.GetValueOrDefault(achievement.Id) + amount;
            }
        }
    }

    private double Value(ProgressState state, string metric) => metric switch
    {
        ProgressMetrics.Level => _catalog.Levels.Level(TotalXp(state)).Level,
        ProgressMetrics.Streak => state.Streak,
        ProgressMetrics.BestStreak => state.BestStreak,
        ProgressMetrics.Achievements => state.Achievements.Count,
        _ => state.Counters.GetValueOrDefault(metric),
    };

    private double Progress(ProgressState state, AchievementDefinition achievement) =>
        achievement.HasHours ? state.HourProgress.GetValueOrDefault(achievement.Id) : Value(state, achievement.Metric);

    /// <summary>Весь опыт: за счётчики по правилам плюс за достижения и цели. Новое правило в данных учитывает и прошлые действия.</summary>
    private long TotalXp(ProgressState state)
    {
        var xp = state.BonusXp;
        foreach (var rule in _catalog.XpRules)
        {
            var steps = Math.Floor(state.Counters.GetValueOrDefault(rule.Metric) / rule.Per);
            xp = Sum(xp, steps >= long.MaxValue / Math.Max(1, rule.Xp) ? long.MaxValue : (long)steps * rule.Xp);
        }

        return xp;
    }

    private AchievementView View(ProgressState state, AchievementDefinition achievement)
    {
        DateTimeOffset? unlocked = state.Achievements.TryGetValue(achievement.Id, out var when) ? when : null;
        var secret = achievement.Hidden && unlocked is null;
        return new AchievementView(
            achievement.Id,
            secret ? string.Empty : achievement.Title,
            secret ? string.Empty : achievement.Text,
            secret ? "lock" : achievement.Icon,
            achievement.Hidden,
            Math.Min(Progress(state, achievement), achievement.Goal),
            achievement.Goal,
            achievement.Reward,
            unlocked);
    }

    private IEnumerable<WeeklyGoalView> Weekly(ProgressState state)
    {
        foreach (var goal in state.Weekly)
        {
            if (_catalog.WeeklyGoals.FirstOrDefault(definition => definition.Id == goal.Id) is { } definition)
            {
                yield return WeeklyView(state, definition, goal);
            }
        }
    }

    private WeeklyGoalView WeeklyView(ProgressState state, WeeklyGoalDefinition definition, WeeklyGoalState goal) => new(
        definition.Id,
        definition.Metric,
        definition.Title,
        definition.Icon,
        Math.Clamp(Value(state, definition.Metric) - goal.Baseline, 0, definition.Goal),
        definition.Goal,
        definition.Reward,
        goal.Completed);

    private DaySummary Summarize(ProgressState state, DayRecord day)
    {
        var best = day.BySource.Where(pair => !OwnSources.Contains(pair.Key) && pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (KeyValuePair<string, long>?)pair)
            .FirstOrDefault();
        return new DaySummary(
            day.Date,
            day.Earned,
            day.Spent,
            day.Clicks,
            day.MediaSeconds,
            Math.Max(0, TotalXp(state) - day.XpStart),
            day.Achievements,
            day.Levels,
            best?.Key,
            best?.Value ?? 0,
            day.Earned > 0 && day.Earned > state.BestDayEarned,
            state.Streak);
    }

    private DateOnly Today() => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    private static long Sum(long total, long amount) => amount > long.MaxValue - total ? long.MaxValue : total + amount;

    /// <summary>Награда, которая зачисляется после изменения состояния, и событие о ней.</summary>
    private sealed record Reward(string Source, long Amount, Action<long> Announce);
}
