namespace ADHDWindows.Core.Cosmetics;

/// <summary>Какие коды косметики можно выдать. Отвечает модуль косметики через <see cref="CosmeticsAvailable"/>.</summary>
public sealed record CosmeticsAvailableRequested;

/// <summary>Коды косметики, которых у игрока ещё нет. Без модуля косметики ответа нет и косметика не выдаётся.</summary>
public sealed record CosmeticsAvailable(IReadOnlyList<string> Codes);

/// <summary>Выдать косметику бесплатно: сундук, сезонное событие, звание.</summary>
public sealed record CosmeticGrantRequested(string Code, string Source);
