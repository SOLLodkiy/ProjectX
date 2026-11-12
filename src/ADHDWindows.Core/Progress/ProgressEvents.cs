namespace ADHDWindows.Core.Progress;

/// <summary>
/// Достижение для показа. Скрытое достижение до получения показывается без названия и описания:
/// окно само заменяет их знаками вопроса. <paramref name="Goal"/> — цель счётчика, <paramref name="Progress"/> — сколько набрано.
/// </summary>
public sealed record AchievementView(
    string Id,
    string Title,
    string Text,
    string Icon,
    bool Hidden,
    double Progress,
    double Goal,
    long Reward,
    DateTimeOffset? Unlocked)
{
    public bool IsUnlocked => Unlocked is not null;

    /// <summary>Доля выполнения от 0 до 1.</summary>
    public double Share => IsUnlocked ? 1 : Goal <= 0 ? 0 : Math.Clamp(Progress / Goal, 0, 1);
}

/// <summary>Цель недели: крупная цель с большой наградой, действует с понедельника до конца воскресенья.</summary>
public sealed record WeeklyGoalView(string Id, string Metric, string Title, string Icon, double Progress, double Goal, long Reward, bool Completed)
{
    public double Share => Completed ? 1 : Goal <= 0 ? 0 : Math.Clamp(Progress / Goal, 0, 1);

    /// <summary>Цель в секундах хронометража: показывается часами.</summary>
    public bool IsTime => Metric.EndsWith(".seconds", StringComparison.Ordinal);
}

/// <summary>
/// Итог одного дня. <paramref name="Date"/> — местная дата. <paramref name="BestSource"/> — источник дохода,
/// принёсший больше всего монеток за день, null если монеток не было. <paramref name="Record"/> — лучший день по монеткам.
/// </summary>
public sealed record DaySummary(
    DateOnly Date,
    long Earned,
    long Spent,
    long Clicks,
    double MediaSeconds,
    long Xp,
    int Achievements,
    int Levels,
    string? BestSource,
    long BestSourceEarned,
    bool Record,
    int Streak);

/// <summary>
/// Весь прогресс игрока. <paramref name="LevelXp"/> — опыт внутри текущего уровня, <paramref name="NextLevelXp"/> — сколько нужно до следующего.
/// <paramref name="SkipAvailable"/> — бесплатный пропуск дня серии на этой неделе ещё не использован.
/// </summary>
public sealed record ProgressSnapshot(
    int Level,
    string Rank,
    string RankName,
    long TotalXp,
    long LevelXp,
    long NextLevelXp,
    int Streak,
    int BestStreak,
    bool SkipAvailable,
    long NextStreakReward,
    IReadOnlyList<AchievementView> Achievements,
    IReadOnlyList<WeeklyGoalView> WeeklyGoals,
    DateOnly WeekStart,
    DaySummary Today,
    DaySummary? LastSummary,
    bool Notifications,
    bool ShowDaySummary)
{
    public double LevelShare => NextLevelXp <= 0 ? 1 : Math.Clamp((double)LevelXp / NextLevelXp, 0, 1);

    public int UnlockedCount => Achievements.Count(achievement => achievement.IsUnlocked);
}

/// <summary>Окну нужен прогресс. Владелец прогресса отвечает <see cref="ProgressChanged"/>.</summary>
public sealed record ProgressRequested;

/// <summary>Прогресс изменился заметно (уровень, достижение, серия, цель) или был запрошен.</summary>
public sealed record ProgressChanged(ProgressSnapshot Snapshot);

/// <summary>
/// Получен новый уровень, награда <paramref name="Earned"/> зачислена. <paramref name="RankChanged"/> — вместе с уровнем пришло новое звание.
/// <paramref name="Cosmetic"/> — код косметики, которую даёт звание, или null. <paramref name="Notify"/> — игрок не отключил уведомления.
/// </summary>
public sealed record LevelReached(int Level, string Rank, string RankName, bool RankChanged, long Earned, string? Cosmetic, bool Notify);

/// <summary>Получено достижение, награда <paramref name="Earned"/> зачислена. Приходит ровно один раз на достижение.</summary>
public sealed record AchievementUnlocked(AchievementView Achievement, long Earned, bool Notify);

/// <summary>Засчитан вход нового дня. <paramref name="SkipUsed"/> — пропущенный день закрыт бесплатным пропуском недели.</summary>
public sealed record StreakRewarded(int Days, long Earned, bool SkipUsed, bool Broken, bool Notify);

/// <summary>Цель недели выполнена, награда зачислена один раз.</summary>
public sealed record WeeklyGoalCompleted(WeeklyGoalView Goal, long Earned, bool Notify);

/// <summary>День закончился, готов итог. <paramref name="Show"/> — игрок не отключил показ итога дня.</summary>
public sealed record DaySummaryReady(DaySummary Summary, bool Show);

/// <summary>Игрок поменял настройку прогресса: <see cref="ProgressSettings"/>.</summary>
public sealed record ProgressSettingRequested(string Key, bool Value);

/// <summary>Коды настроек прогресса. Прогресс считается при любых настройках, они меняют только показ.</summary>
public static class ProgressSettings
{
    /// <summary>Уведомления о достижениях, уровнях, серии и целях недели.</summary>
    public const string Notifications = "notifications";

    /// <summary>Показ окна с итогом прошедшего дня.</summary>
    public const string DaySummary = "daySummary";
}
