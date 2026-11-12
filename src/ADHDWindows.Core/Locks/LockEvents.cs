namespace ADHDWindows.Core.Locks;

/// <summary>Что продаётся на экране покупки.</summary>
public enum LockKind
{
    Program,

    /// <summary>Игра из библиотеки лаунчера. Покупается отдельно от лаунчера.</summary>
    Game,

    /// <summary>Сайт в браузере. Поддомены входят в покупку, если у них нет своей записи в каталоге.</summary>
    Site,
}

/// <summary>Заблокированное: <paramref name="Key"/> — постоянный код покупки.</summary>
public sealed record LockOffer(string Key, string Name, long Price, LockKind Kind);

/// <summary>Изменился список заблокированного. Новые записи идут первыми.</summary>
public sealed record LockOffersChanged(IReadOnlyList<LockOffer> Offers);

/// <summary>Нужно показать экран покупки.</summary>
public sealed record LockScreenRequested(string Key);

/// <summary>Игрок нажал кнопку покупки.</summary>
public sealed record UnlockRequested(string Key);

/// <summary>На покупку не хватило монеток.</summary>
public sealed record UnlockDenied(string Key, long Price);

/// <summary>Покупка совершена и действует навсегда.</summary>
public sealed record ItemUnlocked(LockOffer Offer, DateTimeOffset When);

/// <summary>Купленное навсегда: <paramref name="Key"/> — код покупки, дата и цена — как в момент покупки.</summary>
public sealed record CollectionItem(string Key, string Name, LockKind Kind, long Price, DateTimeOffset When);

/// <summary>Сколько всего в каталоге одного вида и сколько из этого куплено.</summary>
public sealed record CollectionCatalog(LockKind Kind, int Total, int Unlocked);

/// <summary>
/// Коллекция купленного и размер каталога. Владелец покупок рассылает её после каждой покупки,
/// при изменении каталога и по запросу <see cref="CollectionRequested"/>.
/// </summary>
public sealed record CollectionChanged(IReadOnlyList<CollectionItem> Items, IReadOnlyList<CollectionCatalog> Catalog);

/// <summary>Модулю нужна коллекция купленного.</summary>
public sealed record CollectionRequested;

/// <summary>
/// Распродажа: уценить случайный сайт или программу из каталога, которые ещё не куплены, на <paramref name="Percent"/>%
/// на <paramref name="Duration"/>. Ответ — <see cref="LockSaleStarted"/> или <see cref="LockSaleUnavailable"/>
/// с тем же <paramref name="Requester"/>; он может прийти из другого потока.
/// </summary>
public sealed record LockSaleRequested(string Requester, int Percent, TimeSpan Duration);

/// <summary>Распродажа началась: <paramref name="Offer"/> с обычной ценой, <paramref name="SalePrice"/> — цена со скидкой.</summary>
public sealed record LockSaleStarted(string Requester, LockOffer Offer, long SalePrice, int Percent, DateTimeOffset Until);

/// <summary>Уценить нечего: всё куплено или уже со скидкой.</summary>
public sealed record LockSaleUnavailable(string Requester);
