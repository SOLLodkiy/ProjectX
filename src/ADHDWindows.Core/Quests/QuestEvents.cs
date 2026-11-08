namespace ADHDWindows.Core.Quests;

/// <summary>Сложность задания. У каждой сложности свой таймер обновления и одно задание за раз.</summary>
public enum QuestTier
{
    Easy,
    Medium,
    Hard,
}

/// <summary>В чём измеряется цель задания: от этого зависит, как окно показывает прогресс.</summary>
public enum QuestUnit
{
    /// <summary>Штуки: клики, покупки.</summary>
    Count,

    /// <summary>Секунды хронометража. Показываются минутами.</summary>
    Seconds,

    /// <summary>Монетки.</summary>
    Coins,
}

/// <summary>
/// Задание для показа. <paramref name="Id"/> уникален для каждой выдачи, <paramref name="Template"/> — код шаблона из данных.
/// <paramref name="Refresh"/> — когда задание сменится новым, выполнено оно или нет.
/// <paramref name="Url"/> — ссылка задания на страницу, которую нужно открыть; null у заданий без страницы.
/// </summary>
public sealed record QuestView(
    string Id,
    string Template,
    QuestTier Tier,
    string Title,
    string Icon,
    double Progress,
    double Goal,
    QuestUnit Unit,
    long Reward,
    DateTimeOffset Issued,
    DateTimeOffset Refresh,
    bool Completed,
    string? Url = null)
{
    /// <summary>Доля выполнения от 0 до 1.</summary>
    public double Share => Completed ? 1 : Goal <= 0 ? 0 : Math.Clamp(Progress / Goal, 0, 1);
}

/// <summary>
/// Текущие задания всех сложностей, по порядку сложности. Приходит при изменениях и по запросу.
/// <paramref name="Chain"/> — сколько заданий выполнено подряд в текущей цепочке, <paramref name="ChainGoal"/> — длина цепочки.
/// </summary>
public sealed record QuestsChanged(IReadOnlyList<QuestView> Quests, int Chain = 0, int ChainGoal = 3);

/// <summary>Окну нужны текущие задания. Владелец заданий отвечает <see cref="QuestsChanged"/>.</summary>
public sealed record QuestsRequested;

/// <summary>Выдано новое задание. <paramref name="Notify"/> — игрок не отключил уведомления.</summary>
public sealed record QuestIssued(QuestView Quest, bool Notify);

/// <summary>Режим разработчика: сменить все задания сейчас, не дожидаясь таймеров.</summary>
public sealed record QuestsRefreshRequested;

/// <summary>Задание выполнено, награда <paramref name="Earned"/> зачислена. Приходит ровно один раз на задание.</summary>
public sealed record QuestCompleted(QuestView Quest, long Earned, bool Notify);
