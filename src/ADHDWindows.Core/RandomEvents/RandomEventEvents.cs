using ADHDWindows.Core.Hardships;

namespace ADHDWindows.Core.RandomEvents;

/// <summary>
/// Описание случайного события. <paramref name="Resources"/> — что событие занимает: события с ресурсами
/// (экран, окна) снимаются, когда начинается усложнение, а пассивные (без ресурсов) мирно доживают своё.
/// <paramref name="Manual"/> — игрок может открыть событие сам, например колесо фортуны из меню трея.
/// Название и описание лежат в текстах: <c>events.{Id}.title</c> и <c>events.{Id}.text</c>.
/// </summary>
public sealed record RandomEventInfo(string Id, HardshipResources Resources, bool Manual = false);

/// <summary>Частота случайных событий относительно интервала из данных.</summary>
public enum RandomEventFrequency
{
    Off,
    Rare,
    Normal,
    Often,
}

/// <summary>Планировщику нужен список событий. Каждый модуль события отвечает <see cref="RandomEventOffered"/>.</summary>
public sealed record RandomEventsRequested;

/// <summary>Модуль события готов к работе.</summary>
public sealed record RandomEventOffered(RandomEventInfo Event);

/// <summary>
/// Планировщик просит начать событие. <paramref name="Variant"/> — вариант (например, редкость сундука) или null,
/// тогда модуль выбирает сам. <paramref name="Ends"/> — когда планировщик закончит событие сам, если модуль не успеет.
/// Приходит не в потоке интерфейса.
/// </summary>
public sealed record RandomEventStartRequested(string Id, string? Variant, DateTimeOffset Ends);

/// <summary>Планировщик просит убрать событие. Повторная просьба безопасна.</summary>
public sealed record RandomEventStopRequested(string Id);

/// <summary>Модуль закончил событие сам: игрок успел (<paramref name="Success"/>) или нет. <paramref name="Reward"/> — зачисленные монетки.</summary>
public sealed record RandomEventFinished(string Id, bool Success, long Reward);

/// <summary>
/// Модуль не может начать событие: <paramref name="Reason"/> = <c>unavailable</c> — сейчас нечего показать
/// (не сезон, бесплатный спин уже использован, нечего уценить), иначе ошибка.
/// </summary>
public sealed record RandomEventFailed(string Id, string Reason);

/// <summary>Событие началось и закончится не позже <paramref name="Ends"/>.</summary>
public sealed record RandomEventStarted(RandomEventInfo Event, string? Variant, DateTimeOffset Ends);

public enum RandomEventEndReason
{
    /// <summary>Игрок успел: поймал, победил, ответил верно.</summary>
    Completed,

    /// <summary>Время вышло или игрок ошибся.</summary>
    Missed,

    /// <summary>Снято из-за усложнения, «Не беспокоить» или ограничений безопасности.</summary>
    Interrupted,

    /// <summary>Снято игроком, настройкой или остановкой программы.</summary>
    Cancelled,

    /// <summary>Модуль не смог начать событие.</summary>
    Failed,
}

/// <summary>Событие закончилось. <paramref name="Reward"/> — монетки, зачисленные модулем.</summary>
public sealed record RandomEventEnded(RandomEventInfo Event, RandomEventEndReason Reason, long Reward);

/// <summary>Событие и его настройка.</summary>
public sealed record RandomEventEntry(RandomEventInfo Event, bool Enabled);

/// <summary>
/// Состояние событий для меню. <paramref name="Active"/> — идущее событие или null, <paramref name="Next"/> — когда
/// планируется следующее, <paramref name="Blocked"/> — код причины, по которой события сейчас не начинаются.
/// </summary>
public sealed record RandomEventStatus(
    RandomEventFrequency Frequency,
    IReadOnlyList<RandomEventEntry> Events,
    RandomEventInfo? Active,
    DateTimeOffset? Next,
    string? Blocked);

public sealed record RandomEventStatusRequested;

/// <summary>Игрок сменил частоту событий.</summary>
public sealed record RandomEventFrequencyRequested(RandomEventFrequency Frequency);

/// <summary>Игрок включил или выключил одно событие.</summary>
public sealed record RandomEventToggleRequested(string Id, bool Enabled);

/// <summary>Игрок сам открыл событие с <see cref="RandomEventInfo.Manual"/>, например колесо фортуны.</summary>
public sealed record RandomEventOpenRequested(string Id);

/// <summary>
/// Событие-награда в очередь: начнётся при первой возможности, не мешая усложнению и «Не беспокоить».
/// Очередь переживает перезапуск. Например, сундук за цепочку заданий.
/// </summary>
public sealed record RandomEventQueueRequested(string Id, string? Variant, string Reason);

/// <summary>Режим разработчика: начать событие сейчас. Null — случайное.</summary>
public sealed record RandomEventTestRequested(string? Id, string? Variant = null);

/// <summary>Особые варианты событий.</summary>
public static class RandomEventVariants
{
    /// <summary>Игрок открыл событие сам (<see cref="RandomEventOpenRequested"/>).</summary>
    public const string Manual = "manual";
}
