namespace ADHDWindows.Core.Cosmetics;

/// <summary>Где живёт питомец.</summary>
public enum PetPlace
{
    /// <summary>Рядом с HUD, а если HUD скрыт — над панелью задач.</summary>
    Hud,

    /// <summary>Над панелью задач.</summary>
    Taskbar,
}

/// <summary>Настроение питомца — какая анимация играет.</summary>
public enum PetMood
{
    Idle,
    Happy,
    Eat,
    Sleep,
}

/// <summary>
/// Состояние питомца. <paramref name="Species"/> — код надетого вида (косметика места Pet), null — питомца нет.
/// <paramref name="Food"/> — сколько раз его покормили, <paramref name="NextLevelFood"/> — сколько нужно для следующего уровня,
/// null — уровень максимальный. <paramref name="FeedPrice"/> — цена одной порции.
/// </summary>
public sealed record PetState(
    string? Species,
    bool Enabled,
    PetPlace Place,
    int Level,
    int MaxLevel,
    long Food,
    long? NextLevelFood,
    long FeedPrice,
    PetMood Mood);

public sealed record PetRequested;

public sealed record PetChanged(PetState State);

/// <summary>Покормить питомца: списывает цену порции.</summary>
public sealed record PetFeedRequested;

/// <summary>Питомца покормили. <paramref name="Grew"/> — он вырос до уровня <paramref name="Level"/>.</summary>
public sealed record PetFed(string Species, long Price, int Level, bool Grew);

/// <summary>Покормить не удалось: не хватает монеток, питомца нет или он выключен.</summary>
public sealed record PetFeedDenied(string Reason);

/// <summary>Показывать ли питомца и где.</summary>
public sealed record PetSettingsRequested(bool Enabled, PetPlace Place);

/// <summary>Питомец отреагировал на событие: радуется, ест, уснул.</summary>
public sealed record PetReacted(PetMood Mood, string Reason);
