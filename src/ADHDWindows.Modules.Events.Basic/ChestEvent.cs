using System.Globalization;
using ADHDWindows.Core.Cosmetics;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.RandomEvents;
using ADHDWindows.Core.Stats;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Events.Basic;

/// <summary>
/// Возможное содержимое сундука. <paramref name="Kind"/>: <c>coins</c> (от <paramref name="Min"/> до
/// <paramref name="Max"/>), <c>booster</c> (весь доход ×<paramref name="Multiplier"/> на <paramref name="Minutes"/>),
/// <c>cosmetic</c> (случайная косметика, которой ещё нет; без модуля косметики не выпадает).
/// </summary>
public sealed record ChestLoot(string Kind, double Weight, long Min = 0, long Max = 0, decimal Multiplier = 1, double Minutes = 0);

/// <summary>Редкость сундука: шанс появиться (<paramref name="Weight"/>) и сколько предметов в нём.</summary>
public sealed record ChestTier(double Weight, int Rolls, IReadOnlyList<ChestLoot> Loot);

public sealed record ChestSettings(IReadOnlyDictionary<string, ChestTier> Tiers, double Seconds = 12, int Scale = 4)
{
    public static ChestSettings Default { get; } = new(new Dictionary<string, ChestTier>
    {
        [ChestTiers.Common] = new(80, 1, [new ChestLoot("coins", 1, 40, 90)]),
        [ChestTiers.Rare] = new(17, 2, [new ChestLoot("coins", 1, 120, 250)]),
        [ChestTiers.Legendary] = new(3, 3, [new ChestLoot("coins", 1, 400, 800)]),
    });
}

/// <summary>
/// Открывает сундуки: выбирает содержимое по данным и выдаёт его. Монетки идут через общую формулу дохода
/// (источник <see cref="EventSources.Chest"/>), бустер — через модуль событий, косметика — через модуль косметики.
/// </summary>
public sealed class ChestOpener(IEconomy economy, IEventBus events, TextCatalog texts, ILog log, Random random)
{
    private ChestSettings _settings = ChestSettings.Default;
    private volatile IReadOnlyList<string> _cosmetics = [];

    public ChestSettings Settings => _settings;

    public void Load(GameData data)
    {
        var loaded = EventData.Load(data, "events/chests", null, ChestSettings.Default, log);
        var tiers = new Dictionary<string, ChestTier>(ChestSettings.Default.Tiers, StringComparer.Ordinal);
        foreach (var (id, tier) in loaded.Tiers ?? new Dictionary<string, ChestTier>())
        {
            var loot = (tier?.Loot ?? []).Where(Valid).ToList();
            if (tier is { Weight: >= 0, Rolls: > 0 } && loot.Count > 0 && ChestTiers.All.Contains(id))
            {
                tiers[id] = tier with { Loot = loot };
            }
            else
            {
                log.Error($"Неверный сундук {id}, действует значение по умолчанию");
            }
        }

        _settings = loaded with { Tiers = tiers };
    }

    /// <summary>Коды косметики, которых у игрока нет: от модуля косметики.</summary>
    public void SetCosmetics(IReadOnlyList<string> codes) => _cosmetics = codes;

    /// <summary>Редкость по шансам из данных.</summary>
    public string RandomTier()
    {
        var tiers = _settings.Tiers.Where(pair => pair.Value.Weight > 0).ToList();
        var roll = random.NextDouble() * tiers.Sum(pair => pair.Value.Weight);
        foreach (var (id, tier) in tiers)
        {
            roll -= tier.Weight;
            if (roll < 0)
            {
                return id;
            }
        }

        return ChestTiers.Common;
    }

    /// <summary>Открывает сундук и выдаёт содержимое. Возвращает его.</summary>
    public IReadOnlyList<ChestItem> Open(string tier, string reason)
    {
        if (!_settings.Tiers.TryGetValue(tier, out var definition))
        {
            tier = ChestTiers.Common;
            definition = _settings.Tiers[tier];
        }

        var items = new List<ChestItem>();
        var cosmetics = _cosmetics.ToList();
        for (var roll = 0; roll < definition.Rolls; roll++)
        {
            var loot = Pick(definition.Loot.Where(item => item.Kind != "cosmetic" || cosmetics.Count > 0).ToList());
            if (loot is null)
            {
                continue;
            }

            switch (loot.Kind)
            {
                case "coins":
                    var coins = economy.Earn(EventSources.Chest, EventData.Between(random, loot.Min, loot.Max));
                    items.Add(new ChestItem("coins", texts.Get("events.chest.item.coins", CoinFormat.Short(coins)), coins));
                    break;
                case "booster":
                    events.Publish(new BoosterGrantRequested("chest", loot.Multiplier, TimeSpan.FromMinutes(loot.Minutes)));
                    items.Add(new ChestItem("booster", texts.Get(
                        "events.chest.item.booster",
                        loot.Multiplier.ToString("0.##", CultureInfo.CurrentCulture),
                        loot.Minutes.ToString("0", CultureInfo.CurrentCulture))));
                    break;
                case "cosmetic":
                    var code = cosmetics[random.Next(cosmetics.Count)];
                    cosmetics.Remove(code);
                    events.Publish(new CosmeticGrantRequested(code, $"chest.{tier}"));
                    items.Add(new ChestItem("cosmetic", texts.Get("events.chest.item.cosmetic", texts.Get($"cosmetics.{code}.title")), Code: code));
                    break;
            }
        }

        log.Info($"Сундук {tier} ({reason}): {string.Join(", ", items.Select(item => item.Text))}");
        events.Publish(new StatCounted("chests.opened"));
        events.Publish(new StatCounted($"chests.{tier}"));
        events.Publish(new ChestOpened(tier, items, reason));
        return items;
    }

    private static bool Valid(ChestLoot loot) => loot.Weight > 0 && loot.Kind switch
    {
        "coins" => loot.Min > 0 && loot.Max >= loot.Min,
        "booster" => loot.Multiplier > 1 && loot.Minutes > 0,
        "cosmetic" => true,
        _ => false,
    };

    private ChestLoot? Pick(List<ChestLoot> loot)
    {
        var roll = random.NextDouble() * loot.Sum(item => item.Weight);
        foreach (var item in loot)
        {
            roll -= item.Weight;
            if (roll < 0)
            {
                return item;
            }
        }

        return loot.LastOrDefault();
    }
}

/// <summary>
/// Сундук: появляется на экране, клик открывает его. Редкость — обычный, редкий или легендарный по шансам из данных.
/// Сундук-награда (вариант <c>редкость:reward</c>, например за цепочку заданий) не пропадает: если игрок не успел,
/// он открывается сам. Этот же модуль открывает сундуки, купленные у торговца и выигранные на колесе.
/// </summary>
public sealed class ChestEvent : OverlayEvent
{
    public const string RewardSuffix = ":reward";
    private const double OpenSeconds = 1.5;

    private ChestOpener? _opener;
    private SpriteOverlay? _sprite;
    private Picture? _shown;
    private ScreenPoint _home;
    private string _tier = ChestTiers.Common;
    private bool _reward;
    private double _age;
    private double _openFor;
    private long _earned;
    private bool _opened;
    private bool _over;

    public override RandomEventInfo Info { get; } = new("chest", HardshipResources.Screen);

    internal ChestOpener Opener => _opener!;

    internal string Tier => _tier;

    internal bool Opened => Overlay.Invoke(() => _opened);

    internal void ClickForTest() => Overlay.Invoke(() => OnClick(1, 1));

    protected override void OnStart()
    {
        _opener = new ChestOpener(Context.Economy, Context.Events, Context.Texts, Context.Log, Random.Shared);
        _opener.Load(Context.Data);
        Context.Events.Subscribe<CosmeticsAvailable>(available => _opener.SetCosmetics(available.Codes));
        Context.Events.Subscribe<ChestGrantRequested>(request => _opener.Open(request.Tier, request.Reason));

        // Три задания подряд — бонусный сундук (идея 008). Он ждёт в очереди, пока экран свободен.
        Context.Events.Subscribe<QuestChainCompleted>(_ => Context.Events.Publish(
            new RandomEventQueueRequested(Info.Id, ChestTiers.Rare + RewardSuffix, "quests.chain")));
        Context.Events.Publish(new CosmeticsAvailableRequested());
    }

    protected override bool Prepare(string? variant)
    {
        _reward = variant?.EndsWith(RewardSuffix, StringComparison.Ordinal) == true;
        var tier = _reward ? variant![..^RewardSuffix.Length] : variant;
        _tier = tier is not null && ChestTiers.All.Contains(tier) ? tier : _opener!.RandomTier();
        Context.Events.Publish(new CosmeticsAvailableRequested());
        return true;
    }

    protected override void Create()
    {
        _age = 0;
        _openFor = 0;
        _earned = 0;
        _opened = false;
        _over = false;
        _shown = null;
        var picture = EventPictures.Chest(_tier, false);
        var scale = Scale;
        _home = EventArea.RandomSpot(Random, EventArea.WorkArea(), picture.Width * scale, picture.Height * scale);
        _sprite = Overlay.CreateSprite(OnClick);
        Draw(_sprite, picture, scale, _home, ref _shown);
    }

    protected override void Tick(double seconds)
    {
        if (_sprite is null || _over)
        {
            return;
        }

        _age += seconds;
        if (_opened)
        {
            _openFor -= seconds;
            if (_openFor <= 0)
            {
                _over = true;
                _sprite.Hide();
                Complete(true, _earned);
            }

            return;
        }

        if (_age >= _opener!.Settings.Seconds)
        {
            if (_reward)
            {
                // Награду нельзя потерять: сундук открывается сам.
                OnClick(0, 0);
                return;
            }

            _over = true;
            _sprite.Hide();
            Complete(false, 0);
            return;
        }

        // Сундук подпрыгивает раз в секунду, а легендарный ещё и блестит.
        var phase = _age % 1.0;
        var hop = phase < 0.2 ? -(int)(Math.Sin(phase / 0.2 * Math.PI) * 3 * Scale) : 0;
        Draw(_sprite, EventPictures.Chest(_tier, false), Scale, _home with { Y = _home.Y + hop }, ref _shown);
    }

    protected override void Finish() => _sprite = null;

    private int Scale => _opener?.Settings.Scale is > 0 and var scale ? scale : 4;

    private void OnClick(int x, int y)
    {
        if (_opened || _over || _sprite is null)
        {
            return;
        }

        _opened = true;
        _openFor = OpenSeconds;
        var items = _opener!.Open(_tier, _reward ? "reward" : "event");
        _earned = items.Sum(item => item.Coins);
        Draw(_sprite, EventPictures.Chest(_tier, true), Scale, _home, ref _shown);
    }
}
