using ADHDWindows.Core.Cosmetics;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Modules.Cosmetics;

/// <summary>
/// Что есть у игрока и что на нём надето. Покупка только списывает монетки: косметика не влияет на доход.
/// Методы можно вызывать из любого потока. Предметы, которых больше нет в данных, остаются в сохранении.
/// </summary>
public sealed class Wardrobe
{
    public const string OwnedKey = "owned";
    public const string EquippedKey = "equipped";
    public const string EffectsKey = "effects";
    public const string SpendReasonPrefix = "cosmetic:";

    /// <summary>Запись «на этом месте ничего не надето» в сохранении: отличается от «ещё не выбирали».</summary>
    private const string Nothing = "";

    private readonly Dictionary<string, CosmeticItem> _items;
    private readonly IReadOnlyList<CosmeticItem> _order;
    private readonly IEconomy _economy;
    private readonly IEventBus _events;
    private readonly IKeyValueStore _saves;
    private readonly IKeyValueStore _settings;
    private readonly TimeProvider _clock;
    private readonly ILog _log;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _owned;
    private readonly Dictionary<string, string> _equipped;
    private bool _effects;

    public Wardrobe(IReadOnlyList<CosmeticItem> items, IEconomy economy, IEventBus events, IKeyValueStore saves, IKeyValueStore settings, TimeProvider clock, ILog log)
    {
        _order = items;
        _items = items.ToDictionary(item => item.Code, StringComparer.Ordinal);
        _economy = economy;
        _events = events;
        _saves = saves;
        _settings = settings;
        _clock = clock;
        _log = log;
        _owned = new HashSet<string>(saves.Get<List<string>>(OwnedKey) ?? [], StringComparer.Ordinal);
        _equipped = new Dictionary<string, string>(saves.Get<Dictionary<string, string>>(EquippedKey) ?? [], StringComparer.Ordinal);
        _effects = settings.Get(EffectsKey, true);

        // Первый питомец появляется сразу: остальные открываются за уровни, сундуки и монетки.
        if (!_equipped.ContainsKey(nameof(CosmeticSlot.Pet))
            && items.FirstOrDefault(item => item.Slot == CosmeticSlot.Pet && item.Starter) is { } starter)
        {
            _equipped[nameof(CosmeticSlot.Pet)] = starter.Code;
        }
    }

    public bool Owns(string code)
    {
        lock (_lock)
        {
            return OwnsLocked(code);
        }
    }

    public CosmeticItem? Equipped(CosmeticSlot slot)
    {
        lock (_lock)
        {
            return EquippedLocked(slot);
        }
    }

    /// <summary>Рассылает состояние косметики.</summary>
    public void Publish() => _events.Publish(Snapshot());

    public CosmeticsChanged Snapshot()
    {
        lock (_lock)
        {
            var equipped = Enum.GetValues<CosmeticSlot>()
                .Select(slot => (slot, item: EquippedLocked(slot)))
                .Where(pair => pair.item is not null)
                .ToDictionary(pair => pair.slot, pair => pair.item!);
            var offers = _order
                .Select(item => new CosmeticOffer(item, OwnsLocked(item.Code), equipped.GetValueOrDefault(item.Slot)?.Code == item.Code))
                .ToList();
            return new CosmeticsChanged(offers, equipped, _effects);
        }
    }

    /// <summary>Коды, которые можно выдать: всё, чего у игрока нет, и отдельно то, что выпадает из сундуков.</summary>
    public CosmeticsAvailable Available()
    {
        lock (_lock)
        {
            var missing = _order.Where(item => !OwnsLocked(item.Code)).ToList();
            return new CosmeticsAvailable(
                missing.Select(item => item.Code).ToList(),
                missing.Where(item => item.Chest).Select(item => item.Code).ToList());
        }
    }

    public CosmeticDenial? Buy(string code)
    {
        CosmeticDenial? denial = null;
        CosmeticPurchased? purchased = null;
        lock (_lock)
        {
            if (!_items.TryGetValue(code, out var item))
            {
                denial = CosmeticDenial.Unknown;
            }
            else if (OwnsLocked(code))
            {
                denial = CosmeticDenial.Owned;
            }
            else if (!item.ForSale)
            {
                denial = CosmeticDenial.NotForSale;
            }
            else if (!_economy.TrySpend(item.Price, SpendReasonPrefix + code))
            {
                denial = CosmeticDenial.NotEnoughCoins;
            }
            else
            {
                // Купленное сразу надевается: так покупку видно.
                _owned.Add(code);
                _equipped[item.Slot.ToString()] = code;
                SaveLocked();
                purchased = new CosmeticPurchased(code, item.Price, _clock.GetUtcNow());
            }
        }

        if (purchased is null)
        {
            _events.Publish(new CosmeticPurchaseDenied(code, denial!.Value));
            return denial;
        }

        _log.Info($"Куплена косметика {code} за {purchased.Price}");
        _events.Publish(new SaveRequested());
        _events.Publish(purchased);
        Publish();
        return null;
    }

    /// <summary>Выдаёт предмет бесплатно. False — такого нет в данных или он уже есть.</summary>
    public bool Grant(string code, string source)
    {
        CosmeticItem? item;
        lock (_lock)
        {
            if (!_items.TryGetValue(code, out item) || OwnsLocked(code))
            {
                return false;
            }

            _owned.Add(code);
            SaveLocked();
        }

        _log.Info($"Получена косметика {code}: {source}");
        _events.Publish(new SaveRequested());
        _events.Publish(new CosmeticGranted(item, source));
        Publish();
        return true;
    }

    /// <summary>Выдаёт всё, что открывается на уровне <paramref name="level"/> и ниже.</summary>
    public void ReachLevel(int level)
    {
        List<string> codes;
        lock (_lock)
        {
            codes = _order
                .Where(item => item.UnlockLevel > 0 && item.UnlockLevel <= level && !OwnsLocked(item.Code))
                .Select(item => item.Code)
                .ToList();
        }

        codes.ForEach(code => Grant(code, $"level.{level}"));
    }

    /// <summary>Надевает предмет или снимает (null). False — предмета нет у игрока или он для другого места.</summary>
    public bool Equip(CosmeticSlot slot, string? code)
    {
        lock (_lock)
        {
            if (code is not null && (!_items.TryGetValue(code, out var item) || item.Slot != slot || !OwnsLocked(code)))
            {
                return false;
            }

            var key = slot.ToString();
            if (_equipped.GetValueOrDefault(key) == (code ?? Nothing))
            {
                return true;
            }

            _equipped[key] = code ?? Nothing;
            SaveLocked();
        }

        _log.Info($"Косметика {slot}: {code ?? "стандартная"}");
        Publish();
        return true;
    }

    public void SetEffects(bool enabled)
    {
        lock (_lock)
        {
            if (_effects == enabled)
            {
                return;
            }

            _effects = enabled;
            _settings.Set(EffectsKey, enabled);
        }

        Publish();
    }

    private bool OwnsLocked(string code) =>
        _owned.Contains(code) || (_items.TryGetValue(code, out var item) && item.Starter);

    private CosmeticItem? EquippedLocked(CosmeticSlot slot) =>
        _equipped.TryGetValue(slot.ToString(), out var code) && _items.TryGetValue(code, out var item) && OwnsLocked(code)
            ? item
            : null;

    private void SaveLocked()
    {
        _saves.Set(OwnedKey, _owned.Order(StringComparer.Ordinal).ToList());
        _saves.Set(EquippedKey, new Dictionary<string, string>(_equipped));
    }
}
