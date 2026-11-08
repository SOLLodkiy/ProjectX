using ADHDWindows.Core.Locks;

namespace ADHDWindows.Core.Stats;

/// <summary>
/// Счётчик статистики от любой механики, например <c>quests.completed.easy</c> или <c>hardships.survived</c>.
/// Подпись счётчика берётся из текста <c>stats.counter.&lt;код&gt;</c>, поэтому новый счётчик не требует правки окна.
/// Счётчики с кодом <c>quests.*</c> показываются на вкладке «Задания», остальные — в сводке.
/// </summary>
public sealed record StatCounted(string Key, long Amount = 1);

/// <summary>Запись в ленту событий от любой механики. Текст уже готов к показу, <paramref name="Icon"/> — спрайт.</summary>
public sealed record FeedPosted(string Icon, string Text);

/// <summary>Окну нужна свежая статистика. Владелец статистики отвечает <see cref="StatsChanged"/>.</summary>
public sealed record StatsRequested;

public sealed record StatsChanged(StatsSnapshot Snapshot);

/// <summary>Прогресс сброшен, модули запущены заново с пустым сохранением.</summary>
public sealed record ProgressReset;

/// <summary>Сколько времени занимал сайт, программа или игра, в секундах.</summary>
public sealed record UsageEntry(string Name, double Seconds);

/// <summary>Купленный уровень улучшения: дата и цена.</summary>
public sealed record UpgradeRecord(string Id, string Name, int Level, long Price, DateTimeOffset When);

/// <summary>Итоги одного дня. <paramref name="Date"/> — местная дата.</summary>
public sealed record DayStats(DateOnly Date, long Earned, long Spent, long Clicks, double MediaSeconds);

/// <summary>Запись ленты: время, спрайт и готовый текст.</summary>
public sealed record FeedEntry(DateTimeOffset When, string Icon, string Text);

/// <summary>
/// Вся статистика игрока на момент <paramref name="Taken"/>. Хранится только на этом компьютере.
/// Клики по кнопкам: left, right, middle. Время хронометража по источникам: video, music, game.
/// </summary>
public sealed record StatsSnapshot(
    DateTimeOffset Taken,
    DateTimeOffset Since,
    long Balance,
    long TotalEarned,
    long TotalSpent,
    DayStats? BestDay,
    IReadOnlyDictionary<string, long> EarnedBySource,
    IReadOnlyDictionary<string, long> Clicks,
    int BestClicksPerSecond,
    double CursorMeters,
    double AppSeconds,
    IReadOnlyDictionary<string, double> MediaSeconds,
    IReadOnlyList<UsageEntry> TopSites,
    IReadOnlyList<UsageEntry> TopPrograms,
    IReadOnlyList<UsageEntry> TopGames,
    IReadOnlyList<CollectionItem> Collection,
    IReadOnlyList<CollectionCatalog> Catalog,
    IReadOnlyList<UpgradeRecord> Upgrades,
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyList<DayStats> Days,
    IReadOnlyList<FeedEntry> Feed)
{
    /// <summary>Доля открытого каталога от 0 до 1. Пустой каталог — ноль.</summary>
    public double CatalogShare
    {
        get
        {
            var total = Catalog.Sum(part => part.Total);
            return total == 0 ? 0 : Math.Min(1, (double)Catalog.Sum(part => part.Unlocked) / total);
        }
    }

    public long ClicksTotal => Clicks.Values.Sum();
}
