namespace ADHDWindows.Core.Locks;

/// <summary>Что продаётся на экране покупки.</summary>
public enum LockKind
{
    Program,
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
