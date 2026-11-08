namespace ADHDWindows.Core.Upgrades;

/// <summary>Купленный уровень улучшения. Уровень не превышает максимальный из данных.</summary>
public sealed record UpgradeLevel(UpgradeDefinition Upgrade, int Level);

/// <summary>
/// Купленные улучшения изменились или были запрошены. Модуль, который понимает эффект, находит свои записи
/// через <see cref="UpgradeEffects"/> и применяет их сразу. В списке только улучшения с уровнем от 1.
/// </summary>
public sealed record UpgradesChanged(IReadOnlyList<UpgradeLevel> Owned);

/// <summary>Модулю нужны текущие улучшения и магазин. Владелец магазина отвечает обоими событиями.</summary>
public sealed record UpgradesRequested;

/// <summary>Чего не хватает для покупки: улучшение <paramref name="Id"/> нужного уровня.</summary>
public sealed record UpgradeRequirement(string Id, string Name, int Level);

/// <summary>
/// Товар магазина. <paramref name="NextPrice"/> равен null, если уровень максимальный.
/// <paramref name="Missing"/> — невыполненные требования: пока список не пуст, улучшение закрыто.
/// </summary>
public sealed record UpgradeOffer(UpgradeDefinition Upgrade, int Level, long? NextPrice, IReadOnlyList<UpgradeRequirement> Missing)
{
    public bool IsMaxed => NextPrice is null;
    public bool IsLocked => Missing.Count > 0;
}

public sealed record UpgradeShopBranch(string Id, string Name, string Icon, IReadOnlyList<UpgradeOffer> Offers);

/// <summary>Состояние магазина для окна. Приходит после каждой покупки.</summary>
public sealed record UpgradeShopChanged(IReadOnlyList<UpgradeShopBranch> Branches);

/// <summary>Игрок нажал кнопку покупки.</summary>
public sealed record UpgradePurchaseRequested(string Id);

public enum UpgradeDenial
{
    /// <summary>Улучшения нет в магазине.</summary>
    Unknown,

    MaxLevel,

    /// <summary>Не куплены нужные уровни других улучшений.</summary>
    Requirements,

    NotEnoughCoins,
}

public sealed record UpgradePurchaseDenied(string Id, UpgradeDenial Reason);

/// <summary>Куплен уровень <paramref name="Level"/>. Действует сразу и навсегда.</summary>
public sealed record UpgradePurchased(string Id, string Name, int Level, long Price, DateTimeOffset When);
