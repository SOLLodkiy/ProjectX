namespace ADHDWindows.Core.Cosmetics;

/// <summary>
/// Какие коды косметики можно выдать. Отвечает модуль косметики через <see cref="CosmeticsAvailable"/>.
/// </summary>
public sealed record CosmeticsAvailableRequested;

/// <summary>
/// Коды косметики, которых у игрока ещё нет. Без модуля косметики ответа нет и косметика не выдаётся.
/// <paramref name="ChestCodes"/> — те из них, что могут выпасть из сундука; null — любые из <paramref name="Codes"/>.
/// </summary>
public sealed record CosmeticsAvailable(IReadOnlyList<string> Codes, IReadOnlyList<string>? ChestCodes = null);

/// <summary>Выдать косметику бесплатно: сундук, сезонное событие, звание.</summary>
public sealed record CosmeticGrantRequested(string Code, string Source);

/// <summary>Куда надевается косметика. В каждом месте надет один предмет или ничего (стандартный вид).</summary>
public enum CosmeticSlot
{
    Cursor,
    Theme,
    Coin,
    Trail,
    Click,
    Purchase,
    Pet,
}

public enum CosmeticRarity
{
    Common,
    Rare,
    Epic,
    Legendary,
}

/// <summary>
/// Предмет косметики из data/cosmetics. Название и описание лежат в текстах: <c>cosmetics.{Code}.title</c> и <c>.text</c>.
/// <paramref name="Price"/> — цена в магазине, 0 — не продаётся. <paramref name="Chest"/> — может выпасть из сундука.
/// <paramref name="UnlockLevel"/> — выдаётся бесплатно на этом уровне, 0 — нет. <paramref name="Starter"/> — есть у игрока с начала.
/// <paramref name="Animations"/> — кадры картинок по названию анимации: строки символами палитры (assets/palette.json).
/// <paramref name="Colors"/> — цвета палитры по названию (темы), <paramref name="Recolor"/> — замена символов палитры (скины монет).
/// </summary>
public sealed record CosmeticItem(
    string Code,
    CosmeticSlot Slot,
    CosmeticRarity Rarity,
    long Price,
    bool Chest,
    int UnlockLevel,
    bool Starter,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Animations,
    int Fps,
    int HotX,
    int HotY,
    IReadOnlyDictionary<string, string> Colors,
    IReadOnlyDictionary<char, char> Recolor)
{
    /// <summary>Название основной анимации: курсор, монетка и эффекты состоят из неё одной.</summary>
    public const string DefaultAnimation = "default";

    public bool ForSale => Price > 0 && UnlockLevel == 0 && !Starter;

    /// <summary>Кадры анимации или основной, если такой нет. Пустой список — картинок нет (тема, скин монет).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Frames(string animation) =>
        Animations.TryGetValue(animation, out var frames) ? frames
        : Animations.TryGetValue(DefaultAnimation, out var fallback) ? fallback
        : Animations.Values.FirstOrDefault() ?? [];
}

/// <summary>Предмет для окна: есть ли он у игрока и надет ли.</summary>
public sealed record CosmeticOffer(CosmeticItem Item, bool Owned, bool Equipped);

/// <summary>
/// Состояние косметики. Приходит после любого изменения и по запросу <see cref="CosmeticsRequested"/>.
/// <paramref name="Equipped"/> — надетые предметы по местам; места без предмета в словаре нет.
/// <paramref name="EffectsEnabled"/> — эффекты курсора, кликов и покупок показываются.
/// </summary>
public sealed record CosmeticsChanged(
    IReadOnlyList<CosmeticOffer> Items,
    IReadOnlyDictionary<CosmeticSlot, CosmeticItem> Equipped,
    bool EffectsEnabled);

public sealed record CosmeticsRequested;

/// <summary>Игрок нажал «Купить».</summary>
public sealed record CosmeticPurchaseRequested(string Code);

public enum CosmeticDenial
{
    Unknown,
    NotForSale,
    Owned,
    NotEnoughCoins,
}

public sealed record CosmeticPurchaseDenied(string Code, CosmeticDenial Reason);

public sealed record CosmeticPurchased(string Code, long Price, DateTimeOffset When);

/// <summary>Предмет получен бесплатно: сундук, сезон, уровень. <paramref name="Source"/> — откуда.</summary>
public sealed record CosmeticGranted(CosmeticItem Item, string Source);

/// <summary>Надеть предмет. <paramref name="Code"/> null — снять предмет с места <paramref name="Slot"/>.</summary>
public sealed record CosmeticEquipRequested(CosmeticSlot Slot, string? Code);

/// <summary>Включить или выключить эффекты курсора, кликов и покупок.</summary>
public sealed record CosmeticEffectsRequested(bool Enabled);
