using System.Text.Json;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;
using ADHDWindows.Core.Upgrades;
using ADHDWindows.Modules.Clicks;
using ADHDWindows.Modules.Time;
using ADHDWindows.Modules.Upgrades;

namespace ADHDWindows.Tests;

public sealed class UpgradeCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();

    public void Dispose() => _temp.Dispose();

    internal static IReadOnlyList<UpgradeBranch> LoadRepository(RecordingLog log)
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), log);
        data.Load();
        return UpgradeCatalog.Load(data, log);
    }

    private IReadOnlyList<UpgradeBranch> Load(params (string Name, string Json)[] files)
    {
        foreach (var (name, json) in files)
        {
            var path = _temp.Combine("upgrades", name + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        var data = new GameData(_temp.Path, _log);
        data.Load();
        return UpgradeCatalog.Load(data, _log);
    }

    private static string Branch(string id, int order, params string[] upgrades) =>
        $$"""{ "branch": { "id": "{{id}}", "name": "{{id}}", "order": {{order}} }, "upgrades": [ {{string.Join(',', upgrades)}} ] }""";

    private static string Upgrade(string id, string requires = "{}", long price = 10, string growth = "1.5", int maxLevel = 3, string effect = "income.bonus") =>
        $$"""{ "id": "{{id}}", "name": "{{id}}", "effect": "{{effect}}", "price": {{price}}, "growth": {{growth}}, "maxLevel": {{maxLevel}}, "requires": {{requires}} }""";

    [Fact]
    public void Repository_data_has_click_right_button_and_time_upgrades()
    {
        var branches = LoadRepository(_log);
        Assert.Empty(_log.Errors);
        Assert.Equal(["clicks", "time"], branches.Select(branch => branch.Id));

        var all = branches.SelectMany(branch => branch.Upgrades).ToList();
        Assert.Contains(all, upgrade => upgrade.Effect == UpgradeEffects.IncomeBonus && upgrade.Strings("source").Contains(ClicksModule.IncomeSource));
        Assert.Contains(all, upgrade => upgrade.Effect == ClicksModule.ButtonEffect && upgrade.Text("button") == "right");
        Assert.Contains(all, upgrade => upgrade.Effect == TimeModule.CompletionBonusEffect);
        foreach (var source in new[] { "video", "music", "game" })
        {
            Assert.Contains(all, upgrade => upgrade.Effect == UpgradeEffects.IncomeMultiplier && upgrade.Strings("source").Contains(source));
        }
    }

    [Fact]
    public void Repository_upgrades_use_known_effects_sources_and_sprites()
    {
        var effects = new[] { UpgradeEffects.IncomeBonus, UpgradeEffects.IncomeMultiplier, ClicksModule.ButtonEffect, TimeModule.CompletionBonusEffect };
        var sources = new[] { ClicksModule.IncomeSource, ClicksModule.RightSource, ClicksModule.MiddleSource, "video", "music", "game" };
        var sprites = File.ReadAllLines(Path.Combine(RepositoryRoot.Find(), "assets", "src", "sprites", "icons.txt"))
            .Where(line => line.StartsWith("sprite ", StringComparison.Ordinal))
            .Select(line => line.Split(' ')[1])
            .ToHashSet();

        foreach (var branch in LoadRepository(_log))
        {
            Assert.Contains(branch.Icon, sprites);
            foreach (var upgrade in branch.Upgrades)
            {
                Assert.Contains(upgrade.Effect, effects);
                Assert.Contains(upgrade.Icon, sprites);
                Assert.All(upgrade.Strings("source"), source => Assert.Contains(source, sources));
                Assert.NotEmpty(upgrade.Description);
            }
        }
    }

    [Fact]
    public void Branches_are_ordered_and_keep_record_order()
    {
        var branches = Load(
            ("b", Branch("second", 2, Upgrade("b.one"))),
            ("a", Branch("first", 1, Upgrade("a.two"), Upgrade("a.one"))));

        Assert.Equal(["first", "second"], branches.Select(branch => branch.Id));
        Assert.Equal(["a.two", "a.one"], branches[0].Upgrades.Select(upgrade => upgrade.Id));
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Bad_records_are_skipped_without_breaking_others()
    {
        var branches = Load(("x", Branch(
            "x",
            1,
            Upgrade("ok"),
            Upgrade("ok"),
            Upgrade("Bad Id"),
            Upgrade("cheap", price: -1),
            Upgrade("shrinking", growth: "0.5"),
            Upgrade("endless", maxLevel: 0),
            """{ "id": "nameless", "effect": "income.bonus", "price": 1 }""")));

        Assert.Equal(["ok"], branches.Single().Upgrades.Select(upgrade => upgrade.Id));
        Assert.Equal(6, _log.Errors.Count);
    }

    [Fact]
    public void Impossible_requirements_drop_the_record_and_its_dependents()
    {
        var branches = Load(("x", Branch(
            "x",
            1,
            Upgrade("base", maxLevel: 2),
            Upgrade("too.high", requires: """{ "base": 3 }"""),
            Upgrade("missing", requires: """{ "ghost": 1 }"""),
            Upgrade("after.missing", requires: """{ "missing": 1 }"""),
            Upgrade("loop.a", requires: """{ "loop.b": 1 }"""),
            Upgrade("loop.b", requires: """{ "loop.a": 1 }"""),
            Upgrade("fine", requires: """{ "base": 2 }"""))));

        Assert.Equal(["base", "fine"], branches.Single().Upgrades.Select(upgrade => upgrade.Id));
    }

    [Fact]
    public void Price_grows_by_the_multiplier_for_each_level()
    {
        var upgrade = Load(("x", Branch("x", 1, Upgrade("u", price: 100, growth: "1.5", maxLevel: 200)))).Single().Upgrades.Single();

        Assert.Equal(100, upgrade.PriceAt(0));
        Assert.Equal(150, upgrade.PriceAt(1));
        Assert.Equal(225, upgrade.PriceAt(2));
        Assert.Equal(338, upgrade.PriceAt(3));
        Assert.Equal(long.MaxValue, upgrade.PriceAt(199));
    }
}

public sealed class UpgradeShopTests
{
    private readonly RecordingLog _log = new();
    private readonly EventBus _bus;
    private readonly MemoryStore _saves = new();
    private readonly CoinEconomy _economy;
    private readonly List<UpgradesChanged> _changes = [];
    private readonly List<UpgradePurchased> _purchases = [];
    private readonly List<UpgradePurchaseDenied> _denials = [];
    private int _saveRequests;

    public UpgradeShopTests()
    {
        _bus = new EventBus(_log);
        _economy = new CoinEconomy(_saves, _bus, TimeProvider.System);
        _bus.Subscribe<UpgradesChanged>(change => { lock (_changes) { _changes.Add(change); } });
        _bus.Subscribe<UpgradePurchased>(purchase => { lock (_purchases) { _purchases.Add(purchase); } });
        _bus.Subscribe<UpgradePurchaseDenied>(denial => { lock (_denials) { _denials.Add(denial); } });
        _bus.Subscribe<SaveRequested>(_ => Interlocked.Increment(ref _saveRequests));
    }

    private UpgradeShop Shop() => new(UpgradeCatalogTests.LoadRepository(_log), _economy, _bus, _saves, TimeProvider.System, _log);

    private void Grant(long coins) => _economy.Earn("developer", coins);

    [Fact]
    public void Purchase_spends_the_price_and_raises_the_level()
    {
        var shop = Shop();
        Grant(1000);
        var price = shop.Snapshot()[0].Offers.Single(offer => offer.Upgrade.Id == "clicks.left.plus").NextPrice!.Value;

        Assert.Null(shop.Buy("clicks.left.plus"));

        Assert.Equal(1, shop.LevelOf("clicks.left.plus"));
        Assert.Equal(1000 - price, _economy.Balance);
        var purchased = Assert.Single(_purchases);
        Assert.Equal(("clicks.left.plus", 1, price), (purchased.Id, purchased.Level, purchased.Price));
        Assert.Equal(1, _saveRequests);
        Assert.Equal(1, Assert.Single(_changes.Last().Owned).Level);
    }

    [Fact]
    public void Next_level_costs_more()
    {
        var shop = Shop();
        Grant(10_000);
        var first = shop.Snapshot()[0].Offers[0].NextPrice!.Value;
        shop.Buy("clicks.left.plus");
        var second = shop.Snapshot()[0].Offers[0].NextPrice!.Value;

        Assert.True(second > first);
    }

    [Fact]
    public void Not_enough_coins_changes_nothing()
    {
        var shop = Shop();
        Grant(5);

        Assert.Equal(UpgradeDenial.NotEnoughCoins, shop.Buy("clicks.left.plus"));
        Assert.Equal(0, shop.LevelOf("clicks.left.plus"));
        Assert.Equal(5, _economy.Balance);
        Assert.Equal(UpgradeDenial.NotEnoughCoins, Assert.Single(_denials).Reason);
    }

    [Fact]
    public void Requirements_must_be_bought_first()
    {
        var shop = Shop();
        Grant(100_000);

        var offer = shop.Snapshot()[0].Offers.Single(item => item.Upgrade.Id == "clicks.right");
        Assert.True(offer.IsLocked);
        Assert.Equal("clicks.left.plus", Assert.Single(offer.Missing).Id);
        Assert.Equal(UpgradeDenial.Requirements, shop.Buy("clicks.right"));
        Assert.Equal(100_000, _economy.Balance);

        shop.Buy("clicks.left.plus");
        Assert.Null(shop.Buy("clicks.right"));
    }

    [Fact]
    public void Max_level_cannot_be_exceeded()
    {
        var shop = Shop();
        Grant(1_000_000);
        shop.Buy("clicks.left.plus");

        Assert.Null(shop.Buy("clicks.right"));
        Assert.Equal(UpgradeDenial.MaxLevel, shop.Buy("clicks.right"));
        var offer = shop.Snapshot()[0].Offers.Single(item => item.Upgrade.Id == "clicks.right");
        Assert.True(offer.IsMaxed);
        Assert.Null(offer.NextPrice);
    }

    [Fact]
    public void Unknown_upgrade_is_refused() =>
        Assert.Equal(UpgradeDenial.Unknown, Shop().Buy("nothing"));

    [Fact]
    public void Levels_survive_a_restart_and_unknown_levels_are_kept()
    {
        _saves.Set($"{UpgradeShop.LevelsKey}", new Dictionary<string, int> { ["removed.from.data"] = 2 });
        var shop = Shop();
        Grant(1000);
        shop.Buy("clicks.left.plus");

        var restarted = Shop();

        Assert.Equal(1, restarted.LevelOf("clicks.left.plus"));
        Assert.Equal(2, _saves.Get<Dictionary<string, int>>(UpgradeShop.LevelsKey)!["removed.from.data"]);
        Assert.Single(restarted.Owned());
    }

    [Fact]
    public void Saved_level_above_the_maximum_is_capped()
    {
        _saves.Set(UpgradeShop.LevelsKey, new Dictionary<string, int> { ["clicks.right"] = 7, ["clicks.left.plus"] = 1 });
        var shop = Shop();

        Assert.Equal(1, shop.LevelOf("clicks.right"));
        Assert.Equal(UpgradeDenial.MaxLevel, shop.Buy("clicks.right"));
    }

    [Fact]
    public void Two_purchases_from_different_threads_buy_one_level_each()
    {
        var shop = Shop();
        var price = shop.Snapshot()[0].Offers[0].NextPrice!.Value;
        Grant(price);

        var results = new UpgradeDenial?[8];
        Parallel.For(0, results.Length, index => results[index] = shop.Buy("clicks.left.plus"));

        Assert.Equal(1, results.Count(result => result is null));
        Assert.Equal(1, shop.LevelOf("clicks.left.plus"));
        Assert.Equal(0, _economy.Balance);
    }
}

public sealed class IncomeEffectsTests
{
    private readonly CoinEconomy _economy = new(new MemoryStore(), new EventBus(new RecordingLog()), TimeProvider.System);

    private static UpgradeLevel Owned(string effect, string parameters, int level) => new(
        new UpgradeDefinition(
            "u." + effect + parameters.GetHashCode(),
            "x",
            "u",
            string.Empty,
            "star",
            effect,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters)!,
            1,
            1,
            10,
            new Dictionary<string, int>()),
        level);

    [Fact]
    public void Bonus_adds_to_the_base_of_its_source_only()
    {
        using var effects = new IncomeEffects(_economy);
        effects.Apply([Owned(UpgradeEffects.IncomeBonus, """{ "source": "click", "amount": 1 }""", 3)]);

        Assert.Equal(4m, _economy.Preview("click", 1));
        Assert.Equal(1m, _economy.Preview("video", 1));
    }

    [Fact]
    public void Multipliers_apply_after_bonuses_and_multiply_each_other()
    {
        using var effects = new IncomeEffects(_economy);
        effects.Apply(
        [
            Owned(UpgradeEffects.IncomeBonus, """{ "source": "click", "amount": 1 }""", 1),
            Owned(UpgradeEffects.IncomeMultiplier, """{ "source": ["click", "video"], "percent": 25 }""", 2),
            Owned(UpgradeEffects.IncomeMultiplier, """{ "percent": 10 }""", 1),
        ]);

        Assert.Equal(3.3m, _economy.Preview("click", 1));
        Assert.Equal(16.5m, _economy.Preview("video", 10));
        Assert.Equal(1.1m, _economy.Preview("music", 1));
    }

    [Fact]
    public void Applying_again_replaces_instead_of_stacking()
    {
        using var effects = new IncomeEffects(_economy);
        var owned = new[] { Owned(UpgradeEffects.IncomeBonus, """{ "source": "click", "amount": 1 }""", 2) };
        effects.Apply(owned);
        effects.Apply(owned);

        Assert.Equal(3m, _economy.Preview("click", 1));
    }

    [Fact]
    public void Dispose_removes_all_effects()
    {
        var effects = new IncomeEffects(_economy);
        effects.Apply([Owned(UpgradeEffects.IncomeMultiplier, """{ "percent": 100 }""", 1)]);
        effects.Dispose();

        Assert.Equal(1m, _economy.Preview("click", 1));
    }
}

/// <summary>Магазин и клики загружаются из папки modules, как в программе, и общаются только через шину.</summary>
public sealed class UpgradeModulesTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly string _modules;
    private readonly string _data;
    private EventBus _bus = null!;
    private CoinEconomy _economy = null!;
    private JsonFileStore _store = null!;
    private ModuleHost _host = null!;
    private UpgradeShopChanged? _shop;

    public UpgradeModulesTests()
    {
        _modules = _temp.Combine("modules");
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Upgrades", _modules);
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Clicks", _modules);

        _data = _temp.Combine("data");
        Directory.CreateDirectory(Path.Combine(_data, "upgrades"));
        foreach (var file in Directory.GetFiles(Path.Combine(RepositoryRoot.Find(), "data", "upgrades")))
        {
            File.Copy(file, Path.Combine(_data, "upgrades", Path.GetFileName(file)));
        }

        Start();
    }

    public void Dispose()
    {
        _host.StopAll();
        _temp.Dispose();
    }

    private void Start()
    {
        _bus = new EventBus(_log);
        var data = new GameData(_data, _log);
        data.Load();
        _store = new JsonFileStore(_temp.Combine("save.json"), _log);
        _store.Load();
        _economy = new CoinEconomy(_store, _bus, TimeProvider.System);
        _economy.Load();
        _bus.Subscribe<UpgradeShopChanged>(change => _shop = change);
        _host = new ModuleHost(_bus, data, new TextCatalog(data, "ru", _log), _economy, _store, _store, new RecoveryService(new ChangeJournal(_temp.Combine("recovery.json"), _log), () => [], _log), _log, developerMode: false);
        _host.Start(ModuleLoader.Load(_modules, _log));
    }

    private void Restart()
    {
        _host.StopAll();
        _store.Flush();
        Start();
    }

    private double _time;
    private int _clicks;

    private void Click(MouseButton button)
    {
        // Неровные промежутки, чтобы защита от автокликеров не сработала.
        _time += 300 + (_clicks++ * 37 % 100);
        _bus.Publish(new MouseButtonChanged(button, true, false, TimeSpan.FromMilliseconds(_time)));
        _bus.Publish(new MouseButtonChanged(button, false, false, TimeSpan.FromMilliseconds(_time + 40)));
    }

    private long Earned(MouseButton button)
    {
        var before = _economy.Balance;
        Click(button);
        return _economy.Balance - before;
    }

    private void Buy(string id) => _bus.Publish(new UpgradePurchaseRequested(id));

    [Fact]
    public void Shop_starts_and_publishes_its_state()
    {
        Assert.Equal(["clicks", "upgrades"], _host.Running.Select(module => module.Id).Order());
        Assert.Empty(_log.Errors);
        Assert.NotNull(_shop);
        Assert.Equal(["clicks", "time"], _shop.Branches.Select(branch => branch.Id));
    }

    [Fact]
    public void Click_upgrade_works_at_once_and_after_restart()
    {
        _economy.Earn("developer", 1000);
        Assert.Equal(1, Earned(MouseButton.Left));

        Buy("clicks.left.plus");
        Assert.Equal(2, Earned(MouseButton.Left));

        Restart();
        Assert.Equal(2, Earned(MouseButton.Left));
        Assert.Equal(1, _shop!.Branches[0].Offers.Single(offer => offer.Upgrade.Id == "clicks.left.plus").Level);
    }

    [Fact]
    public void Right_and_middle_buttons_earn_only_after_their_upgrades()
    {
        _economy.Earn("developer", 100_000);
        Assert.Equal(0, Earned(MouseButton.Right));
        Assert.Equal(0, Earned(MouseButton.Middle));

        Buy("clicks.left.plus");
        Buy("clicks.right");
        Assert.Equal(1, Earned(MouseButton.Right));
        Assert.Equal(0, Earned(MouseButton.Middle));

        Buy("clicks.right.plus");
        Assert.Equal(2, Earned(MouseButton.Right));
        Assert.Equal(2, Earned(MouseButton.Left));

        Buy("clicks.middle");
        Assert.Equal(1, Earned(MouseButton.Middle));

        Restart();
        Assert.Equal(2, Earned(MouseButton.Right));
        Assert.Equal(1, Earned(MouseButton.Middle));
    }

    [Fact]
    public void Synthetic_right_clicks_earn_nothing_even_after_the_upgrade()
    {
        _economy.Earn("developer", 100_000);
        Buy("clicks.left.plus");
        Buy("clicks.right");
        var before = _economy.Balance;

        _bus.Publish(new MouseButtonChanged(MouseButton.Right, true, true, TimeSpan.FromSeconds(100)));
        _bus.Publish(new MouseButtonChanged(MouseButton.Right, false, true, TimeSpan.FromSeconds(100.1)));

        Assert.Equal(before, _economy.Balance);
    }

    [Fact]
    public void New_upgrade_on_a_ready_effect_is_one_data_record()
    {
        // Новая ветка с одной записью: ни ядро, ни модули не меняются.
        File.WriteAllText(Path.Combine(_data, "upgrades", "extra.json"), """
            {
              "branch": { "id": "extra", "name": "Проверка", "icon": "star", "order": 9 },
              "upgrades": [
                { "id": "extra.music", "name": "Громче", "effect": "income.multiplier",
                  "params": { "source": "music", "percent": 50 }, "price": 10 }
              ]
            }
            """);
        Restart();
        _economy.Earn("developer", 10);
        Assert.Equal(2m, _economy.Preview("music", 2));

        Buy("extra.music");

        Assert.Equal(3m, _economy.Preview("music", 2));
        Assert.Equal("extra", _shop!.Branches[^1].Id);
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Stopped_shop_removes_its_effects()
    {
        _economy.Earn("developer", 1000);
        Buy("clicks.left.plus");
        Assert.Equal(2m, _economy.Preview(ClicksModule.IncomeSource, 1));

        _host.StopAll();

        Assert.Equal(1m, _economy.Preview(ClicksModule.IncomeSource, 1));
    }
}

internal sealed class MemoryStore : IKeyValueStore
{
    private readonly Dictionary<string, string> _values = [];
    private readonly Lock _lock = new();

    public T? Get<T>(string key, T? fallback = default)
    {
        lock (_lock)
        {
            return _values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : fallback;
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _values[key] = JsonSerializer.Serialize(value);
        }
    }

    public bool Remove(string key)
    {
        lock (_lock)
        {
            return _values.Remove(key);
        }
    }
}
