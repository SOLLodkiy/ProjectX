namespace ADHDWindows.Core.Hardships;

/// <summary>Сложность усложнения. Чем сложнее, тем короче оно длится.</summary>
public enum HardshipLevel
{
    Easy,
    Medium,
    Hard,
}

/// <summary>Режим усложнений. От него зависят частота, сложность и множитель всего дохода.</summary>
public enum HardshipMode
{
    /// <summary>Усложнений нет, доход ×0,5.</summary>
    Off,

    /// <summary>Только лёгкие, доход ×0,75.</summary>
    Soft,

    /// <summary>Все усложнения, доход ×1.</summary>
    Normal,

    /// <summary>Чаще и сложнее, доход ×1,5.</summary>
    Hardcore,
}

/// <summary>
/// Что занимает усложнение. Два усложнения с общим ресурсом не могут работать одновременно,
/// а планировщик и другие механики по этим флагам решают, что можно совмещать.
/// </summary>
[Flags]
public enum HardshipResources
{
    None = 0,

    /// <summary>Положение или скорость курсора.</summary>
    CursorMotion = 1,

    /// <summary>Вид системного курсора.</summary>
    CursorLook = 2,

    /// <summary>Окна других программ.</summary>
    Windows = 4,

    /// <summary>Рисует поверх экрана.</summary>
    Screen = 8,

    /// <summary>Кнопки мыши.</summary>
    MouseButtons = 16,

    /// <summary>Ввод с клавиатуры.</summary>
    Keyboard = 32,

    /// <summary>Проигрывает звуки.</summary>
    Sound = 64,
}

/// <summary>
/// Описание усложнения. Название и описание лежат в текстах: <c>hardships.{Id}.title</c> и <c>hardships.{Id}.text</c>.
/// </summary>
public sealed record HardshipInfo(string Id, HardshipLevel Level, HardshipResources Resources);

/// <summary>Планировщику нужен список усложнений. Каждый модуль усложнения отвечает <see cref="HardshipOffered"/>.</summary>
public sealed record HardshipsRequested;

/// <summary>Модуль усложнения готов к работе.</summary>
public sealed record HardshipOffered(HardshipInfo Hardship);

/// <summary>Планировщик просит включить усложнение. Приходит не в потоке интерфейса.</summary>
public sealed record HardshipStartRequested(string Id);

/// <summary>Планировщик просит снять усложнение. Повторная просьба безопасна.</summary>
public sealed record HardshipStopRequested(string Id);

/// <summary>Модуль не смог включить усложнение: планировщик снимает его без бонуса и выбирает другое позже.</summary>
public sealed record HardshipFailed(string Id, string Reason);

/// <summary>Через <paramref name="Delay"/> начнётся усложнение.</summary>
public sealed record HardshipWarned(HardshipInfo Hardship, TimeSpan Delay);

/// <summary>Усложнение работает до <paramref name="Ends"/>.</summary>
public sealed record HardshipStarted(HardshipInfo Hardship, DateTimeOffset Ends);

/// <summary>Почему усложнение закончилось.</summary>
public enum HardshipEndReason
{
    /// <summary>Время вышло, игрок получает бонус.</summary>
    Survived,

    /// <summary>Снято из-за ограничений безопасности или «Не беспокоить».</summary>
    Interrupted,

    /// <summary>Снято игроком, настройкой или остановкой программы.</summary>
    Cancelled,

    /// <summary>Модуль не смог его включить.</summary>
    Failed,
}

/// <summary>Усложнение закончилось. <paramref name="Bonus"/> — зачисленный бонус, 0 если не пережито.</summary>
public sealed record HardshipEnded(HardshipInfo Hardship, HardshipEndReason Reason, long Bonus);

/// <summary>Усложнение, его настройка и сколько оно длится в текущем режиме.</summary>
public sealed record HardshipEntry(HardshipInfo Hardship, bool Enabled);

/// <summary>
/// Состояние усложнений для меню и окон. Приходит при изменениях и по запросу <see cref="HardshipStatusRequested"/>.
/// <paramref name="Active"/> — работающее усложнение или null. <paramref name="Next"/> — когда планируется следующее.
/// <paramref name="QuietUntil"/> — до какого времени включено «Не беспокоить» вручную.
/// <paramref name="Blocked"/> — код причины, по которой сейчас усложнения не запускаются, или null.
/// </summary>
public sealed record HardshipStatus(
    HardshipMode Mode,
    decimal Multiplier,
    IReadOnlyList<HardshipEntry> Hardships,
    HardshipInfo? Active,
    DateTimeOffset? ActiveEnds,
    DateTimeOffset? Next,
    DateTimeOffset? QuietUntil,
    string? Blocked);

public sealed record HardshipStatusRequested;

/// <summary>Игрок сменил режим.</summary>
public sealed record HardshipModeRequested(HardshipMode Mode);

/// <summary>Игрок включил или выключил одно усложнение.</summary>
public sealed record HardshipToggleRequested(string Id, bool Enabled);

/// <summary>«Не беспокоить» на <paramref name="Duration"/>. Нулевая длительность выключает режим.</summary>
public sealed record HardshipQuietRequested(TimeSpan Duration);

/// <summary>Режим разработчика: запустить усложнение сейчас, без предупреждения. Null — случайное.</summary>
public sealed record HardshipTestRequested(string? Id);

/// <summary>Снять активное усложнение без бонуса. В режиме разработчика или из меню.</summary>
public sealed record HardshipCancelRequested;
