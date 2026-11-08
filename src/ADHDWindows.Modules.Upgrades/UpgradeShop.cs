using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Storage;
using ADHDWindows.Core.Upgrades;

namespace ADHDWindows.Modules.Upgrades;

/// <summary>
/// Магазин улучшений: уровни, цены, требования и покупка. Методы можно вызывать из любого потока.
/// Уровни улучшений, которых больше нет в данных, сохраняются: запись могла пропасть из-за ошибки в файле.
/// </summary>
public sealed class UpgradeShop
{
    public const string LevelsKey = "levels";
    public const string SpendReasonPrefix = "upgrade:";

    private readonly IReadOnlyList<UpgradeBranch> _branches;
    private readonly Dictionary<string, UpgradeDefinition> _upgrades;
    private readonly IEconomy _economy;
    private readonly IEventBus _events;
    private readonly IKeyValueStore _saves;
    private readonly TimeProvider _clock;
    private readonly ILog _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _levels;

    public UpgradeShop(IReadOnlyList<UpgradeBranch> branches, IEconomy economy, IEventBus events, IKeyValueStore saves, TimeProvider clock, ILog log)
    {
        _branches = branches;
        _upgrades = branches.SelectMany(branch => branch.Upgrades).ToDictionary(upgrade => upgrade.Id, StringComparer.Ordinal);
        _economy = economy;
        _events = events;
        _saves = saves;
        _clock = clock;
        _log = log;
        _levels = new Dictionary<string, int>(
            (saves.Get<Dictionary<string, int>>(LevelsKey) ?? []).Where(pair => pair.Value > 0),
            StringComparer.Ordinal);
    }

    public int LevelOf(string id)
    {
        lock (_lock)
        {
            return Level(id);
        }
    }

    /// <summary>Купленные улучшения из данных.</summary>
    public IReadOnlyList<UpgradeLevel> Owned()
    {
        lock (_lock)
        {
            return OwnedLocked();
        }
    }

    public IReadOnlyList<UpgradeShopBranch> Snapshot()
    {
        lock (_lock)
        {
            return SnapshotLocked();
        }
    }

    /// <summary>Рассылает текущие улучшения и состояние магазина.</summary>
    public void Publish()
    {
        IReadOnlyList<UpgradeLevel> owned;
        IReadOnlyList<UpgradeShopBranch> shop;
        lock (_lock)
        {
            owned = OwnedLocked();
            shop = SnapshotLocked();
        }

        _events.Publish(new UpgradesChanged(owned));
        _events.Publish(new UpgradeShopChanged(shop));
    }

    /// <summary>Покупает следующий уровень. Возвращает null при успехе или причину отказа.</summary>
    public UpgradeDenial? Buy(string id)
    {
        UpgradePurchased? purchased = null;
        UpgradeDenial? denial;
        lock (_lock)
        {
            denial = Deny(id);
            if (denial is null)
            {
                // Проверка и списание под одним замком: две быстрые покупки не купят один уровень дважды.
                var upgrade = _upgrades[id];
                var level = Level(id);
                var price = upgrade.PriceAt(level);
                if (_economy.TrySpend(price, SpendReasonPrefix + id))
                {
                    _levels[id] = level + 1;
                    _saves.Set(LevelsKey, new Dictionary<string, int>(_levels));
                    purchased = new UpgradePurchased(id, upgrade.Name, level + 1, price, _clock.GetUtcNow());
                }
                else
                {
                    denial = UpgradeDenial.NotEnoughCoins;
                }
            }
        }

        if (purchased is null)
        {
            _events.Publish(new UpgradePurchaseDenied(id, denial!.Value));
            return denial;
        }

        _log.Info($"Куплено улучшение {id} ур. {purchased.Level} за {purchased.Price}");
        _events.Publish(new SaveRequested());
        _events.Publish(purchased);
        Publish();
        return null;
    }

    private UpgradeDenial? Deny(string id)
    {
        if (!_upgrades.TryGetValue(id, out var upgrade))
        {
            return UpgradeDenial.Unknown;
        }

        if (Level(id) >= upgrade.MaxLevel)
        {
            return UpgradeDenial.MaxLevel;
        }

        return Missing(upgrade).Count > 0 ? UpgradeDenial.Requirements : null;
    }

    private int Level(string id) =>
        _upgrades.TryGetValue(id, out var upgrade) ? Math.Min(_levels.GetValueOrDefault(id), upgrade.MaxLevel) : 0;

    private List<UpgradeRequirement> Missing(UpgradeDefinition upgrade) => upgrade.Requires
        .Where(pair => Level(pair.Key) < pair.Value)
        .Select(pair => new UpgradeRequirement(pair.Key, _upgrades[pair.Key].Name, pair.Value))
        .ToList();

    private List<UpgradeLevel> OwnedLocked() => _upgrades.Values
        .Select(upgrade => new UpgradeLevel(upgrade, Level(upgrade.Id)))
        .Where(item => item.Level > 0)
        .ToList();

    private List<UpgradeShopBranch> SnapshotLocked() => _branches
        .Select(branch => new UpgradeShopBranch(
            branch.Id,
            branch.Name,
            branch.Icon,
            branch.Upgrades.Select(upgrade =>
            {
                var level = Level(upgrade.Id);
                return new UpgradeOffer(upgrade, level, level >= upgrade.MaxLevel ? null : upgrade.PriceAt(level), Missing(upgrade));
            }).ToList()))
        .ToList();
}
