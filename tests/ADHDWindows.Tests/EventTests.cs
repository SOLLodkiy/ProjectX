using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.RandomEvents;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Stats;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Events;
using ADHDWindows.Modules.Events.Basic;
using ADHDWindows.Modules.Locks;
using ADHDWindows.Modules.Locks.Sites;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

/// <summary>Планировщик случайных событий на ручных часах, без экрана.</summary>
public sealed class EventSchedulerTests : IDisposable
{
    private static readonly RandomEventInfo Coin = new("coin", HardshipResources.Screen);
    private static readonly RandomEventInfo Lucky = new("lucky", HardshipResources.None);
    private static readonly RandomEventInfo Boss = new("boss", HardshipResources.Screen);
    private static readonly RandomEventInfo Wheel = new("wheel", HardshipResources.Screen, Manual: true);

    private static readonly EventRules Rules = EventRules.Default with
    {
        IntervalMinutes = 20,
        SpreadMinutes = 0,
        FirstDelayMinutes = 3,
        PostponeMinutes = 3,
        HardshipGapSeconds = 90,
        Events = new Dictionary<string, EventRule>
        {
            ["coin"] = new(1, 10, 0),
            ["lucky"] = new(1, 10, 0),
            ["boss"] = new(1, 50, 1),
            ["wheel"] = new(0, 120, 0),
        },
    };

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly ManualClock _clock = new();
    private readonly EventBus _bus;
    private readonly JsonFileStore _store;
    private readonly TextCatalog _texts;
    private readonly List<object> _events = [];
    private readonly HashSet<string> _unavailable = [];
    private readonly HashSet<string> _running = [];
    private readonly ScopedStore _saves;
    private readonly ScopedStore _settings;
    private Action<RandomEventStartRequested>? _onStart;

    public EventSchedulerTests()
    {
        _bus = new EventBus(_log);
        _store = new JsonFileStore(_temp.Combine("save.json"), _log);
        _store.Load();
        _saves = new ScopedStore(_store, "modules/events");
        _settings = new ScopedStore(_store, "settings/events");
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        _texts = new TextCatalog(data, "ru", _log);
        _texts.Use("ru");
        _bus.Subscribe<RandomEventStarted>(_events.Add);
        _bus.Subscribe<RandomEventEnded>(_events.Add);
        _bus.Subscribe<StatCounted>(_events.Add);
        _bus.Subscribe<RandomEventStatus>(_events.Add);
        _bus.Subscribe<RandomEventStartRequested>(request =>
        {
            if (_unavailable.Contains(request.Id))
            {
                _bus.Publish(new RandomEventFailed(request.Id, "unavailable"));
                return;
            }

            _running.Add(request.Id);
            _onStart?.Invoke(request);
        });
        _bus.Subscribe<RandomEventStopRequested>(request => _running.Remove(request.Id));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Event_starts_on_schedule_and_completes_once_with_stats()
    {
        using var scheduler = Create(Coin);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(20), scheduler.Next);
        GoTo(scheduler, scheduler.Next);

        var started = Assert.Single(_events.OfType<RandomEventStarted>());
        Assert.Equal("coin", started.Event.Id);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromSeconds(10), started.Ends);
        Assert.Equal(["coin"], _running);

        scheduler.OnFinished(new RandomEventFinished("coin", true, 150));
        scheduler.OnFinished(new RandomEventFinished("coin", true, 150));

        var ended = Assert.Single(_events.OfType<RandomEventEnded>());
        Assert.Equal(RandomEventEndReason.Completed, ended.Reason);
        Assert.Equal(150, ended.Reward);
        Assert.Empty(_running);
        Assert.Single(_events.OfType<StatCounted>(), stat => stat.Key == "events.coin");
        Assert.Null(scheduler.Active);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(20), scheduler.Next);
    }

    [Fact]
    public void Event_without_an_answer_ends_as_missed_by_its_deadline()
    {
        using var scheduler = Create(Coin);
        GoTo(scheduler, scheduler.Next);
        Step(scheduler, TimeSpan.FromSeconds(10));

        var ended = Assert.Single(_events.OfType<RandomEventEnded>());
        Assert.Equal(RandomEventEndReason.Missed, ended.Reason);
        Assert.Empty(_running);
        Assert.DoesNotContain(_events.OfType<StatCounted>(), stat => stat.Key == "events.coin");
    }

    [Fact]
    public void Events_wait_for_a_hardship_and_screen_events_give_way_to_it()
    {
        using var scheduler = Create(Coin);
        scheduler.OnHardshipStatus(Status(active: true));
        GoTo(scheduler, scheduler.Next);
        Assert.Empty(_events.OfType<RandomEventStarted>());
        Assert.Equal("hardship", _events.OfType<RandomEventStatus>().Last().Blocked);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(3), scheduler.Next);

        // Усложнение закончилось: ещё полторы минуты тишины.
        scheduler.OnHardshipEnded();
        scheduler.OnHardshipStatus(Status(active: false));
        Step(scheduler, TimeSpan.FromSeconds(1));
        Assert.Equal("hardship", _events.OfType<RandomEventStatus>().Last().Blocked);
        Step(scheduler, TimeSpan.FromSeconds(90));
        Assert.Null(_events.OfType<RandomEventStatus>().Last().Blocked);
        Assert.Empty(_events.OfType<RandomEventStarted>());
        GoTo(scheduler, scheduler.Next);
        Assert.Single(_events.OfType<RandomEventStarted>());

        // Началось усложнение — событие на экране убирается сразу.
        scheduler.OnHardshipStarted();
        var ended = Assert.Single(_events.OfType<RandomEventEnded>());
        Assert.Equal(RandomEventEndReason.Interrupted, ended.Reason);
        Assert.Empty(_running);
    }

    [Fact]
    public void Passive_event_is_not_removed_by_a_hardship_but_nothing_new_starts_around_one()
    {
        using var scheduler = Create(Lucky);
        _onStart = _ => { };
        GoTo(scheduler, scheduler.Next);
        Assert.Equal(["lucky"], _running);
        scheduler.OnHardshipStarted();
        Assert.Equal(["lucky"], _running);
        Assert.Empty(_events.OfType<RandomEventEnded>());
        scheduler.OnFinished(new RandomEventFinished("lucky", true, 0));
        scheduler.OnHardshipEnded();

        // Предупреждение об усложнении и скорое усложнение тоже останавливают события.
        _clock.Advance(scheduler.Next - _clock.GetUtcNow() - TimeSpan.FromSeconds(5));
        scheduler.OnHardshipWarned(new HardshipWarned(new HardshipInfo("x", HardshipLevel.Easy, HardshipResources.None), TimeSpan.FromSeconds(10)));
        Step(scheduler, TimeSpan.FromSeconds(5));
        Assert.Single(_events.OfType<RandomEventStarted>());
        Assert.Equal("hardship", _events.OfType<RandomEventStatus>().Last().Blocked);

        scheduler.OnHardshipEnded();
        Step(scheduler, TimeSpan.FromMinutes(5));
        scheduler.OnHardshipStatus(Status(active: false, next: _clock.GetUtcNow() + TimeSpan.FromSeconds(60)));
        Step(scheduler, TimeSpan.FromSeconds(1));
        Assert.Equal("hardshipSoon", _events.OfType<RandomEventStatus>().Last().Blocked);
    }

    [Fact]
    public void Quiet_mode_and_safety_postpone_events_and_remove_the_running_one()
    {
        using var scheduler = Create(Coin);
        scheduler.OnHardshipStatus(Status(active: false, quietUntil: _clock.GetUtcNow() + TimeSpan.FromMinutes(30)));
        GoTo(scheduler, scheduler.Next);
        Assert.Empty(_events.OfType<RandomEventStarted>());
        Assert.Equal("quiet", _events.OfType<RandomEventStatus>().Last().Blocked);

        scheduler.OnHardshipStatus(Status(active: false));
        GoTo(scheduler, scheduler.Next);
        Assert.Single(_events.OfType<RandomEventStarted>());

        // Полноэкранная игра посреди события.
        scheduler.Tick("fullscreen");
        Assert.Equal(RandomEventEndReason.Interrupted, Assert.Single(_events.OfType<RandomEventEnded>()).Reason);
        Assert.Empty(_running);
        Assert.False(scheduler.Test("coin", null, "fullscreen"));
        Assert.Single(_events.OfType<RandomEventStarted>());
    }

    [Fact]
    public void Unavailable_event_is_replaced_by_another_one()
    {
        _unavailable.Add("coin");
        using var scheduler = Create(Coin, Lucky);
        _onStart = _ => { };
        GoTo(scheduler, scheduler.Next);
        Step(scheduler, TimeSpan.FromSeconds(1));
        Step(scheduler, TimeSpan.FromSeconds(1));

        var started = Assert.Single(_events.OfType<RandomEventStarted>());
        Assert.Equal("lucky", started.Event.Id);
        Assert.DoesNotContain(_events.OfType<RandomEventEnded>(), ended => ended.Event.Id == "coin");
    }

    [Fact]
    public void Daily_limit_holds_until_the_next_day_and_the_same_event_does_not_repeat()
    {
        using var scheduler = Create(Boss, Coin);
        var ids = new List<string>();
        for (var round = 0; round < 6; round++)
        {
            GoTo(scheduler, scheduler.Next);
            var started = _events.OfType<RandomEventStarted>().Last();
            ids.Add(started.Event.Id);
            scheduler.OnFinished(new RandomEventFinished(started.Event.Id, false, 0));
        }

        Assert.Equal(1, ids.Count(id => id == "boss"));
        Assert.All(ids.Zip(ids.Skip(1)), pair => Assert.True(pair.First != pair.Second || pair.First == "coin"));

        Step(scheduler, TimeSpan.FromDays(1));
        for (var round = 0; round < 3; round++)
        {
            GoTo(scheduler, scheduler.Next);
            var started = _events.OfType<RandomEventStarted>().Last();
            ids.Add(started.Event.Id);
            scheduler.OnFinished(new RandomEventFinished(started.Event.Id, false, 0));
        }

        Assert.Equal(2, ids.Count(id => id == "boss"));
    }

    [Fact]
    public void Queued_reward_waits_for_the_hardship_runs_with_events_off_and_survives_a_restart()
    {
        using (var scheduler = Create(Coin))
        {
            scheduler.SetFrequency(RandomEventFrequency.Off);
            scheduler.OnHardshipStatus(Status(active: true));
            scheduler.Enqueue(new RandomEventQueueRequested("coin", "rare:reward", "quests.chain"));
            Step(scheduler, TimeSpan.FromSeconds(1));
            Assert.Empty(_events.OfType<RandomEventStarted>());
        }

        using var again = Create(Coin);
        Assert.Equal(RandomEventFrequency.Off, again.Frequency);
        Assert.Single(again.Queue);
        Step(again, TimeSpan.FromSeconds(1));
        var started = Assert.Single(_events.OfType<RandomEventStarted>());
        Assert.Equal("rare:reward", started.Variant);

        // Усложнение прервало награду — она вернётся позже.
        again.OnHardshipStarted();
        Assert.Single(again.Queue);
        again.OnHardshipEnded();
        Step(again, TimeSpan.FromMinutes(2));
        Assert.Equal(2, _events.OfType<RandomEventStarted>().Count());
        again.OnFinished(new RandomEventFinished("coin", true, 10));
        Assert.Empty(again.Queue);
        Step(again, TimeSpan.FromHours(2));
        Assert.Equal(2, _events.OfType<RandomEventStarted>().Count());
    }

    [Fact]
    public void Disabled_events_and_frequency_change_what_and_when()
    {
        using var scheduler = Create(Coin, Lucky);
        scheduler.SetEnabled("coin", false);
        _onStart = _ => { };
        for (var round = 0; round < 4; round++)
        {
            GoTo(scheduler, scheduler.Next);
            scheduler.OnFinished(new RandomEventFinished("lucky", true, 0));
        }

        Assert.All(_events.OfType<RandomEventStarted>(), started => Assert.Equal("lucky", started.Event.Id));

        scheduler.SetFrequency(RandomEventFrequency.Often);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(10), scheduler.Next);
        scheduler.SetFrequency(RandomEventFrequency.Rare);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(40), scheduler.Next);

        using var again = Create(Coin, Lucky);
        Assert.Equal(RandomEventFrequency.Rare, again.Frequency);
        Assert.Contains(_events.OfType<RandomEventStatus>().Last().Events, entry => entry.Event.Id == "coin" && !entry.Enabled);
    }

    [Fact]
    public void Instant_event_is_announced_before_its_end()
    {
        using var scheduler = Create(Lucky);
        _onStart = request => scheduler.OnFinished(new RandomEventFinished(request.Id, true, 0));
        GoTo(scheduler, scheduler.Next);

        var order = _events.Where(item => item is RandomEventStarted or RandomEventEnded).ToList();
        Assert.IsType<RandomEventStarted>(order[0]);
        Assert.Equal(RandomEventEndReason.Completed, Assert.IsType<RandomEventEnded>(order[1]).Reason);
        Assert.Null(scheduler.Active);
    }

    [Fact]
    public void Manual_event_opens_on_request_but_not_during_a_hardship()
    {
        using var scheduler = Create(Coin, Wheel);
        Assert.False(scheduler.Open("coin"));
        scheduler.OnHardshipStatus(Status(active: true));
        Assert.False(scheduler.Open("wheel"));
        scheduler.OnHardshipStatus(Status(active: false));
        Assert.True(scheduler.Open("wheel"));
        Assert.Equal(RandomEventVariants.Manual, Assert.Single(_events.OfType<RandomEventStarted>()).Variant);

        // Вес 0: само колесо не выпадает.
        scheduler.OnFinished(new RandomEventFinished("wheel", true, 0));
        for (var round = 0; round < 5; round++)
        {
            GoTo(scheduler, scheduler.Next);
            scheduler.OnFinished(new RandomEventFinished("coin", false, 0));
        }

        Assert.Single(_events.OfType<RandomEventStarted>(), started => started.Event.Id == "wheel");
    }

    private static HardshipStatus Status(bool active, DateTimeOffset? quietUntil = null, DateTimeOffset? next = null) =>
        new(HardshipMode.Normal, 1, [], active ? new HardshipInfo("h", HardshipLevel.Easy, HardshipResources.Screen) : null, null, next, quietUntil, null);

    private EventScheduler Create(params RandomEventInfo[] offered)
    {
        var scheduler = new EventScheduler(Rules, _bus, _saves, _settings, _texts, _clock, new Random(7), _log);
        foreach (var info in offered)
        {
            scheduler.Offer(info);
        }

        return scheduler;
    }

    private void Step(EventScheduler scheduler, TimeSpan time)
    {
        _clock.Advance(time);
        scheduler.Tick(null);
    }

    private void GoTo(EventScheduler scheduler, DateTimeOffset when)
    {
        if (when > _clock.GetUtcNow())
        {
            _clock.Advance(when - _clock.GetUtcNow());
        }

        scheduler.Tick(null);
    }
}

/// <summary>Бустеры и распродажи.</summary>
public sealed class BoosterAndSaleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly ManualClock _clock = new();
    private readonly EventBus _bus;
    private readonly JsonFileStore _store;
    private readonly CoinEconomy _economy;
    private readonly TextCatalog _texts;
    private readonly List<HudStatusChanged> _hud = [];

    public BoosterAndSaleTests()
    {
        _bus = new EventBus(_log);
        _store = new JsonFileStore(_temp.Combine("save.json"), _log);
        _store.Load();
        _economy = new CoinEconomy(_store, _bus, _clock);
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        _texts = new TextCatalog(data, "ru", _log);
        _texts.Use("ru");
        _bus.Subscribe<HudStatusChanged>(_hud.Add);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Booster_multiplies_its_source_extends_expires_and_survives_a_restart()
    {
        using (var boosters = new BoosterBook(_economy, _bus, _store, _texts, _clock, _log))
        {
            boosters.Grant(new BoosterGrantRequested("luckyHour", 2, TimeSpan.FromMinutes(5)));
            boosters.Grant(new BoosterGrantRequested("merchant.click", 3, TimeSpan.FromMinutes(12), "click"));
            Assert.Equal(60, _economy.Preview("click", 10));
            Assert.Equal(20, _economy.Preview("video", 10));
            Assert.Equal("Час удачи ×2 5:00 +1", _hud.Last().Text);

            // Повторный бустер продлевает время, а не складывает множители.
            _clock.Advance(TimeSpan.FromMinutes(1));
            boosters.Grant(new BoosterGrantRequested("luckyHour", 2, TimeSpan.FromMinutes(5)));
            Assert.Equal(20, _economy.Preview("video", 10));
            Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(9), boosters.Active.Single(view => view.Id == "luckyHour").Ends);
        }

        Assert.Equal(10, _economy.Preview("click", 10));
        using var again = new BoosterBook(_economy, _bus, _store, _texts, _clock, _log);
        Assert.Equal(60, _economy.Preview("click", 10));

        _clock.Advance(TimeSpan.FromMinutes(9));
        again.Tick();
        Assert.Equal(30, _economy.Preview("click", 10));
        Assert.Equal(10, _economy.Preview("video", 10));
        _clock.Advance(TimeSpan.FromMinutes(2));
        again.Tick();
        Assert.Equal(10, _economy.Preview("click", 10));
        Assert.Empty(again.Active);
        Assert.Null(_hud.Last().Text);

        // Неверный бустер не принимается.
        again.Grant(new BoosterGrantRequested("bad", 0, TimeSpan.FromMinutes(1)));
        Assert.Empty(again.Active);
    }

    [Fact]
    public void Sale_discounts_a_random_unbought_catalog_item_until_it_ends()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        var programs = ProgramCatalog.Load(data);
        var sites = SiteCatalog.Load(data);
        var sales = new SaleBook(_store, _clock);
        var changed = 0;
        sales.Changed += () => changed++;
        var unlocked = new HashSet<string>(sites.Sites.Select(site => SiteClassifier.KeyPrefix + site.Domain).Skip(1));
        unlocked.UnionWith(programs.Programs.Select(program => $"program:{program.Id}"));

        var started = sales.Start(new LockSaleRequested("test", 40, TimeSpan.FromHours(1)), programs, sites, unlocked.Contains, new Random(3));
        Assert.NotNull(started);
        var site = sites.Sites[0];
        Assert.Equal(SiteClassifier.KeyPrefix + site.Domain, started.Offer.Key);
        Assert.Equal(SaleBook.Discounted(site.Price, 40), started.SalePrice);
        Assert.True(started.SalePrice < site.Price);
        Assert.Equal(1, changed);

        // Уценять больше нечего: единственная некупленная запись уже со скидкой.
        Assert.Null(sales.Start(new LockSaleRequested("test", 40, TimeSpan.FromHours(1)), programs, sites, unlocked.Contains, new Random(3)));

        var offer = new LockOffer(started.Offer.Key, site.Name, site.Price, LockKind.Site);
        Assert.Equal(started.SalePrice, sales.Apply(offer).Price);
        Assert.Equal(started.SalePrice, new SaleBook(_store, _clock).Apply(offer).Price);
        Assert.Equal(100, sales.Apply(new LockOffer("other", "x", 100, LockKind.Program)).Price);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(site.Price, sales.Apply(offer).Price);
        Assert.True(sales.Tick());
        Assert.Equal(2, changed);
        Assert.Empty(sales.Active);
        Assert.Equal(1, SaleBook.Discounted(1, 60));
        Assert.Equal(61, SaleBook.Discounted(101, 40));
    }
}

/// <summary>
/// Настоящие модули событий в режиме разработчика: каждое событие запускается вручную, игрок может его пройти,
/// а после конца или снятия на экране ничего не остаётся.
/// </summary>
public sealed class EventModuleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly ConcurrentQueue<object> _seen = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Every_event_starts_by_hand_and_leaves_nothing_behind_when_removed()
    {
        var seasonal = new SeasonalEvent { Today = () => new DateOnly(2026, 10, 31) };
        var modules = AllEvents(seasonal);
        using var harness = new Harness(_temp, _log, _seen, modules);
        Assert.Equal(11, harness.Scheduler.Offered.Count);

        foreach (var module in modules.OfType<RandomEventModule>())
        {
            var id = module.Info.Id;
            harness.Clear();
            harness.Test(id);
            if (module.Info.Resources == HardshipResources.None)
            {
                // Час удачи и распродажа мгновенные: они сразу заканчиваются сами.
                WaitUntil(() => harness.Ended(id) is not null, $"{id} закончилось");
                Assert.Equal(RandomEventEndReason.Completed, harness.Ended(id)!.Reason);
                continue;
            }

            WaitUntil(() => harness.Started(id) is not null, $"{id} началось");
            Assert.True(module.IsActive, id);
            if (module is OverlayEvent overlay)
            {
                Assert.True(overlay.OverlayWindows > 0, $"{id}: окна на экране");
            }
            else
            {
                Assert.Single(harness.Seen<EventDialogShown>());
            }

            harness.Scheduler.Cancel();
            Assert.False(module.IsActive, id);
            Assert.Equal(RandomEventEndReason.Cancelled, harness.Ended(id)!.Reason);
            if (module is OverlayEvent cleared)
            {
                Assert.Equal(0, cleared.OverlayWindows);
            }
            else
            {
                Assert.Equal(harness.Seen<EventDialogShown>().Single().Dialog.Id, Assert.Single(harness.Seen<EventDialogClosed>()).Id);
            }
        }

        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Golden_coin_takes_a_real_click_pays_once_and_disappears()
    {
        var coin = new GoldenCoinEvent();
        using var harness = new Harness(_temp, _log, _seen, [coin]);
        harness.Test("goldenCoin");
        WaitUntil(() => coin.IsActive, "монета");
        var bounds = coin.TargetBounds!.Value;
        var center = new Core.Display.ScreenPoint(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));

        // Клик забирает окно монеты, поэтому окну под ней он не достаётся.
        var before = CursorControl.Position();
        try
        {
            CursorControl.MoveTo(center);
            Thread.Sleep(50);
            Assert.Equal(Environment.ProcessId, ProcessOf(WindowFromPoint(new NativePoint { X = center.X, Y = center.Y })));
            Click();
            WaitUntil(() => harness.Ended("goldenCoin") is not null, "награда");
        }
        finally
        {
            CursorControl.MoveTo(before);
        }

        var ended = harness.Ended("goldenCoin")!;
        Assert.Equal(RandomEventEndReason.Completed, ended.Reason);
        Assert.Equal(150, ended.Reward);
        Assert.Equal(150, harness.Economy.Balance);
        Assert.Equal(0, coin.OverlayWindows);
        Assert.Contains(harness.Seen<StatCounted>(), stat => stat.Key == "events.goldenCoin");
    }

    [Fact]
    public void Coin_rain_is_caught_with_the_cursor_goblin_and_boss_with_clicks()
    {
        var rain = new CoinRainEvent();
        var goblin = new GoblinEvent();
        var boss = new BossEvent();
        using var harness = new Harness(_temp, _log, _seen, [rain, goblin, boss]);

        harness.Test("coinRain");
        WaitUntil(() => rain.IsActive, "дождь");
        rain.Cursor = () => rain.Coins.FirstOrDefault() is { } coin
            ? new Core.Display.ScreenPoint(coin.Left + (coin.Width / 2), coin.Top + (coin.Height / 2))
            : new Core.Display.ScreenPoint(-10000, -10000);
        WaitUntil(() => rain.Caught >= 3, "три монетки");
        harness.Scheduler.Cancel();
        Assert.True(harness.Economy.Balance >= 15);
        Assert.Equal(0, rain.OverlayWindows);

        var balance = harness.Economy.Balance;
        harness.Test("goblin");
        WaitUntil(() => goblin.GoblinBounds is not null, "гоблин");
        goblin.ClickForTest();
        goblin.ClickForTest();
        WaitUntil(() => harness.Ended("goblin") is not null, "мешок");
        Assert.InRange(harness.Economy.Balance - balance, 60, 140);
        Assert.Equal(harness.Economy.Balance - balance, harness.Ended("goblin")!.Reward);

        balance = harness.Economy.Balance;
        harness.Test("boss");
        WaitUntil(() => boss.BossBounds is not null, "босс");
        for (var hit = 0; hit < 29; hit++)
        {
            boss.HitForTest();
        }

        Assert.Equal(1, boss.Health);
        Assert.Null(harness.Ended("boss"));
        boss.HitForTest();
        WaitUntil(() => harness.Ended("boss") is not null, "победа");
        Assert.Equal(RandomEventEndReason.Completed, harness.Ended("boss")!.Reason);
        Assert.Equal(balance + 400, harness.Economy.Balance);
        Assert.Equal(0, boss.OverlayWindows);
    }

    [Fact]
    public void Chests_open_with_their_tier_and_a_quest_chain_queues_a_reward_chest()
    {
        var chest = new ChestEvent();
        using var harness = new Harness(_temp, _log, _seen, [chest]);
        harness.Test("chest", ChestTiers.Legendary);
        WaitUntil(() => chest.IsActive, "сундук");
        Assert.Equal(ChestTiers.Legendary, chest.Tier);
        chest.ClickForTest();
        WaitUntil(() => harness.Ended("chest") is not null, "сундук открыт");

        var opened = Assert.Single(harness.Seen<ChestOpened>());
        Assert.Equal(ChestTiers.Legendary, opened.Tier);
        Assert.Equal(3, opened.Items.Count);
        Assert.DoesNotContain(opened.Items, item => item.Kind == "cosmetic");
        Assert.Equal(opened.Items.Sum(item => item.Coins), harness.Ended("chest")!.Reward);
        Assert.Equal(opened.Items.Sum(item => item.Coins), harness.Economy.Balance);

        // Сундук за покупку открывается сразу, без экрана.
        harness.Bus.Publish(new ChestGrantRequested(ChestTiers.Rare, "merchant"));
        Assert.Equal(2, harness.Seen<ChestOpened>().Count());

        harness.Bus.Publish(new QuestChainCompleted(3));
        var queued = Assert.Single(harness.Scheduler.Queue);
        Assert.Equal("chest", queued.Id);
        Assert.Equal("rare:reward", queued.Variant);
    }

    [Fact]
    public void Lucky_hour_and_sale_work_without_the_screen()
    {
        using var harness = new Harness(_temp, _log, _seen, [new LuckyHourEvent(), new SaleEvent(), new LocksModule()]);
        harness.Test("luckyHour");
        WaitUntil(() => harness.Ended("luckyHour") is not null, "час удачи");
        Assert.Equal(20, harness.Economy.Preview("click", 10));
        Assert.Single(harness.Seen<RandomEventNotice>());

        harness.Test("sale");
        WaitUntil(() => harness.Ended("sale") is not null, "распродажа");
        var sale = Assert.Single(harness.Seen<LockSaleStarted>());
        Assert.InRange(sale.Percent, 30, 60);
        Assert.True(sale.SalePrice < sale.Offer.Price);
    }

    [Fact]
    public void Wheel_gives_one_free_spin_a_day_then_charges_and_opens_by_hand()
    {
        var wheel = new WheelEvent { Today = () => new DateOnly(2026, 11, 12) };
        using var harness = new Harness(_temp, _log, _seen, [wheel, new ChestEvent()]);
        harness.Test("wheel");
        WaitUntil(() => harness.Seen<EventDialogShown>().Any(), "колесо");
        var dialog = harness.Seen<EventDialogShown>().Last().Dialog;
        Assert.Equal(wheel.Segments.Count, dialog.Wheel!.Count);
        Assert.Equal(0, Assert.Single(dialog.Choices).Price);

        harness.Bus.Publish(new EventDialogChosen(dialog.Id, "spin"));
        var spun = harness.Seen<EventDialogShown>().Last().Dialog;
        Assert.NotNull(spun.WheelResult);
        Assert.Equal(1, spun.Spin);
        Assert.False(wheel.FreeSpinAvailable);
        Assert.Equal(100, Assert.Single(spun.Choices).Price);

        // Второй спин стоит монеток: без них колесо не крутится.
        if (harness.Economy.Balance < 100)
        {
            harness.Bus.Publish(new EventDialogChosen(dialog.Id, "spin"));
            Assert.Equal(1, harness.Seen<EventDialogShown>().Last().Dialog.Spin);
            Assert.Contains("100", harness.Seen<EventDialogShown>().Last().Dialog.Text);
            harness.Economy.Earn("developer", 100);
        }

        harness.Bus.Publish(new EventDialogChosen(dialog.Id, "spin"));
        Assert.Equal(2, harness.Seen<EventDialogShown>().Last().Dialog.Spin);
        Assert.Contains(harness.Seen<CoinsSpent>(), spent => spent.Amount == 100);

        harness.Bus.Publish(new EventDialogChosen(dialog.Id, EventDialogChosen.CloseChoice));
        WaitUntil(() => harness.Ended("wheel") is not null, "колесо закрыто");
        Assert.Equal(RandomEventEndReason.Completed, harness.Ended("wheel")!.Reason);

        // Бесплатный спин уже был: само колесо не выпадает, но из меню открывается.
        harness.Clear();
        harness.Test("wheel");
        Assert.Null(harness.Started("wheel"));
        harness.Bus.Publish(new RandomEventOpenRequested("wheel"));
        Assert.NotNull(harness.Started("wheel"));
        Assert.Equal(100, Assert.Single(harness.Seen<EventDialogShown>().Last().Dialog.Choices).Price);
        harness.Scheduler.Cancel();

        // Назавтра снова бесплатно.
        wheel.Today = () => new DateOnly(2026, 11, 13);
        Assert.True(wheel.FreeSpinAvailable);
    }

    [Fact]
    public void Riddle_pays_for_the_right_answer_and_merchant_sells_for_coins()
    {
        var riddle = new RiddleEvent();
        var merchant = new MerchantEvent();
        using var harness = new Harness(_temp, _log, _seen, [riddle, merchant, new ChestEvent()]);

        harness.Test("riddle");
        WaitUntil(() => riddle.Current is not null && harness.Seen<EventDialogShown>().Any(), "загадка");
        var dialog = harness.Seen<EventDialogShown>().Last().Dialog;
        var right = riddle.Order.ToList().IndexOf(riddle.Current!.Correct);
        Assert.Equal(riddle.Current.Answers[riddle.Current.Correct], dialog.Choices[right].Text);
        harness.Bus.Publish(new EventDialogChosen(dialog.Id, $"answer.{right}"));
        WaitUntil(() => harness.Ended("riddle") is not null, "ответ");
        Assert.Equal(RandomEventEndReason.Completed, harness.Ended("riddle")!.Reason);
        Assert.Equal(riddle.Current.Reward, harness.Economy.Balance);

        // Неверный ответ — без награды, но с подсказкой.
        harness.Clear();
        harness.Test("riddle");
        WaitUntil(() => harness.Seen<EventDialogShown>().Any(), "вторая загадка");
        dialog = harness.Seen<EventDialogShown>().Last().Dialog;
        var wrong = Enumerable.Range(0, riddle.Order.Count).First(button => riddle.Order[button] != riddle.Current.Correct);
        harness.Bus.Publish(new EventDialogChosen(dialog.Id, $"answer.{wrong}"));
        WaitUntil(() => harness.Ended("riddle") is not null, "ответ");
        Assert.Equal(RandomEventEndReason.Missed, harness.Ended("riddle")!.Reason);
        Assert.Contains(riddle.Current.Answers[riddle.Current.Correct], harness.Seen<RandomEventNotice>().Last().Text);

        harness.Clear();
        harness.Economy.Earn("developer", 5000);
        harness.Test("merchant");
        WaitUntil(() => harness.Seen<EventDialogShown>().Any(), "торговец");
        dialog = harness.Seen<EventDialogShown>().Last().Dialog;
        Assert.Equal(3, dialog.Choices.Count);
        var offer = merchant.Stock[0];
        harness.Bus.Publish(new EventDialogChosen(dialog.Id, offer.Id));
        harness.Bus.Publish(new EventDialogChosen(dialog.Id, offer.Id));
        Assert.Equal(offer.Price, Assert.Single(harness.Seen<CoinsSpent>()).Amount);
        Assert.False(harness.Seen<EventDialogShown>().Last().Dialog.Choices[0].Enabled);
        harness.Bus.Publish(new EventDialogChosen(dialog.Id, EventDialogChosen.CloseChoice));
        WaitUntil(() => harness.Ended("merchant") is not null, "торговец ушёл");
        Assert.Equal(RandomEventEndReason.Completed, harness.Ended("merchant")!.Reason);
    }

    [Fact]
    public void Seasonal_event_runs_only_in_its_season()
    {
        var seasonal = new SeasonalEvent { Today = () => new DateOnly(2026, 7, 1) };
        using var harness = new Harness(_temp, _log, _seen, [seasonal]);
        harness.Test("seasonal");
        Assert.Null(harness.Started("seasonal"));

        seasonal.Today = () => new DateOnly(2027, 1, 3);
        Assert.Equal("newYear", seasonal.Current!.Id);
        seasonal.Today = () => new DateOnly(2026, 10, 31);
        Assert.Equal("halloween", seasonal.Current!.Id);
        harness.Test("seasonal");
        WaitUntil(() => seasonal.TargetBounds is not null, "тыква");
        seasonal.ClickForTest();
        WaitUntil(() => harness.Ended("seasonal") is not null, "награда");
        Assert.Equal(300, harness.Economy.Balance);
        Assert.Contains(harness.Seen<StatCounted>(), stat => stat.Key == "events.season.halloween");
    }

    [Fact]
    public void Modules_load_from_the_folder_and_the_repository_data_is_clean()
    {
        var folder = _temp.Combine("modules");
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Events", folder);
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Events.Basic", folder);
        var bus = new EventBus(_log);
        var offered = new ConcurrentBag<string>();
        bus.Subscribe<RandomEventOffered>(offer => offered.Add(offer.Event.Id));
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        var texts = new TextCatalog(data, "ru", _log);
        texts.Use("ru");
        var store = new JsonFileStore(_temp.Combine("folder.json"), _log);
        store.Load();
        var host = new ModuleHost(bus, data, texts, new CoinEconomy(store, bus, TimeProvider.System), store, store, new RecoveryService(new ChangeJournal(_temp.Combine("recovery.json"), _log), () => [], _log), _log, developerMode: false);
        host.Start(ModuleLoader.Load(folder, _log));
        try
        {
            Assert.Equal(12, host.Running.Count);
            Assert.Equal(
                ["boss", "chest", "coinRain", "goblin", "goldenCoin", "luckyHour", "merchant", "riddle", "sale", "seasonal", "wheel"],
                offered.Distinct().Order(StringComparer.Ordinal));
            Assert.True(_log.Errors.Count == 0, string.Join(Environment.NewLine, _log.Errors));
            Assert.True(_log.Warnings.Count == 0, string.Join(Environment.NewLine, _log.Warnings));

            // У каждого события есть название и описание.
            foreach (var id in offered.Distinct())
            {
                Assert.NotEqual($"events.{id}.title", texts.Get($"events.{id}.title"));
                Assert.NotEqual($"events.{id}.text", texts.Get($"events.{id}.text"));
            }
        }
        finally
        {
            host.StopAll();
        }
    }

    private static List<IModule> AllEvents(SeasonalEvent seasonal) =>
    [
        new GoldenCoinEvent(), new CoinRainEvent(), new GoblinEvent(), new ChestEvent(), new LuckyHourEvent(), new SaleEvent(),
        new WheelEvent(), new BossEvent(), new RiddleEvent(), new MerchantEvent(), seasonal, new LocksModule(),
    ];

    private static void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(10)), $"Не дождались: {what}");
            Thread.Sleep(20);
        }
    }

    private static void Click()
    {
        const uint leftDown = 0x0002;
        const uint leftUp = 0x0004;
        mouse_event(leftDown, 0, 0, 0, IntPtr.Zero);
        mouse_event(leftUp, 0, 0, 0, IntPtr.Zero);
    }

    private static int ProcessOf(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var process);
        return process;
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Ядро с модулем событий и указанными модулями; ограничения безопасности выключены, как в автотестах усложнений.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly ModuleHost _host;
        private readonly ConcurrentQueue<object> _seen;
        private readonly EventsModule _events = new();

        public Harness(TempDirectory temp, RecordingLog log, ConcurrentQueue<object> seen, IReadOnlyList<IModule> modules)
        {
            _seen = seen;
            Bus = new EventBus(log);
            Bus.Subscribe<RandomEventStarted>(seen.Enqueue);
            Bus.Subscribe<RandomEventEnded>(seen.Enqueue);
            Bus.Subscribe<EventDialogShown>(seen.Enqueue);
            Bus.Subscribe<EventDialogClosed>(seen.Enqueue);
            Bus.Subscribe<ChestOpened>(seen.Enqueue);
            Bus.Subscribe<StatCounted>(seen.Enqueue);
            Bus.Subscribe<RandomEventNotice>(seen.Enqueue);
            Bus.Subscribe<LockSaleStarted>(seen.Enqueue);
            Bus.Subscribe<CoinsSpent>(seen.Enqueue);
            var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), log);
            data.Load();
            var store = new JsonFileStore(temp.Combine($"save-{Guid.NewGuid():N}.json"), log);
            store.Load();
            store.Set("settings/modules/events/" + EventsModule.IgnoreSafetySetting, true);
            Economy = new CoinEconomy(store, Bus, TimeProvider.System);
            var texts = new TextCatalog(data, "ru", log);
            texts.Use("ru");
            _host = new ModuleHost(Bus, data, texts, Economy, store, new ScopedStore(store, "settings"), new RecoveryService(new ChangeJournal(temp.Combine($"recovery-{Guid.NewGuid():N}.json"), log), () => [], log), log, developerMode: true);
            _host.Start([_events, .. modules]);
        }

        public EventBus Bus { get; }

        public CoinEconomy Economy { get; }

        public EventScheduler Scheduler => _events.Scheduler!;

        public void Test(string id, string? variant = null) => Bus.Publish(new RandomEventTestRequested(id, variant));

        public IEnumerable<T> Seen<T>() => _seen.OfType<T>();

        public RandomEventStarted? Started(string id) => Seen<RandomEventStarted>().LastOrDefault(started => started.Event.Id == id);

        public RandomEventEnded? Ended(string id) => Seen<RandomEventEnded>().LastOrDefault(ended => ended.Event.Id == id);

        public void Clear() => _seen.Clear();

        public void Dispose() => _host.StopAll();
    }
}
