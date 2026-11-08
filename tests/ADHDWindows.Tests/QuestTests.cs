using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Stats;
using ADHDWindows.Core.Storage;
using ADHDWindows.Core.Upgrades;
using ADHDWindows.Modules.Quests;

namespace ADHDWindows.Tests;

public sealed class QuestCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private IReadOnlyList<QuestTemplate> Load(string json)
    {
        var path = _temp.Combine("quests", "test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        var data = new GameData(_temp.Path, _log);
        data.Load();
        return QuestCatalog.Load(data, _log);
    }

    [Fact]
    public void Repository_templates_cover_every_tier_and_use_known_sprites()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        var templates = QuestCatalog.Load(data, _log);
        Assert.Empty(_log.Errors);

        var sprites = File.ReadAllLines(Path.Combine(RepositoryRoot.Find(), "assets", "src", "sprites", "icons.txt"))
            .Where(line => line.StartsWith("sprite ", StringComparison.Ordinal))
            .Select(line => line.Split(' ')[1])
            .ToHashSet();
        foreach (var tier in Enum.GetValues<QuestTier>())
        {
            var ofTier = templates.Where(template => template.Tier == tier).ToList();

            // Клики, видео и траты выполнимы всегда: каждой сложности есть что выдать даже без игр и магазина.
            Assert.Contains(ofTier, template => template.Type == QuestTypes.Clicks);
            Assert.Contains(ofTier, template => template.Type == QuestTypes.Media && template.Source == "video");
            Assert.Contains(ofTier, template => template.Type == QuestTypes.Spend);
        }

        Assert.Contains(templates, template => template.Type == QuestTypes.Game);
        Assert.Contains(templates, template => template.Type == QuestTypes.Upgrade);
        Assert.Contains(templates, template => template.Type == QuestTypes.Unlock);
        Assert.All(templates, template => Assert.Contains(template.Icon, sprites));

        // Награды из идеи 008: лёгкие 20–50, средние 75–150, сложные 200–500.
        Assert.All(templates.Where(template => template.Tier == QuestTier.Easy), template => Assert.True(template.RewardMin >= 20 && template.RewardMax <= 50));
        Assert.All(templates.Where(template => template.Tier == QuestTier.Medium), template => Assert.True(template.RewardMin >= 75 && template.RewardMax <= 150));
        Assert.All(templates.Where(template => template.Tier == QuestTier.Hard), template => Assert.True(template.RewardMin >= 200 && template.RewardMax <= 500));
    }

    [Fact]
    public void Broken_templates_are_skipped_and_the_rest_load()
    {
        var templates = Load("""
            { "templates": [
              { "id": "ok", "tier": "easy", "type": "clicks", "goal": 10, "reward": [20, 30], "title": "Клики" },
              { "id": "ok", "tier": "easy", "type": "clicks", "goal": 10, "reward": [20, 30], "title": "Повтор" },
              { "id": "type", "tier": "easy", "type": "dance", "goal": 10, "reward": [20, 30], "title": "Танцуй" },
              { "id": "tier", "tier": "epic", "type": "clicks", "goal": 10, "reward": [20, 30], "title": "Клики" },
              { "id": "goal", "tier": "easy", "type": "clicks", "goal": 0, "reward": [20, 30], "title": "Клики" },
              { "id": "reward", "tier": "easy", "type": "clicks", "goal": 10, "reward": [30, 20], "title": "Клики" },
              { "id": "game", "tier": "hard", "type": "game", "goal": 10, "reward": [200, 300], "title": "Поиграй" },
              { "id": "source", "tier": "easy", "type": "media", "source": "radio", "goal": 10, "reward": [20, 30], "title": "Слушай" }
            ] }
            """);

        var only = Assert.Single(templates);
        Assert.Equal("ok", only.Id);
        Assert.Equal("quest", only.Icon);
        Assert.Equal(7, _log.Errors.Count);
    }

    [Fact]
    public void Minutes_become_seconds_of_progress()
    {
        var media = new QuestTemplate("m", QuestTier.Easy, QuestTypes.Media, 5, 20, 30, "Видео", "clock");
        Assert.Equal(QuestUnit.Seconds, media.Unit);
        Assert.Equal(300, media.GoalValue);
        Assert.Equal("1:05/5:00", QuestFormat.Progress(new QuestView("x", "m", QuestTier.Easy, "", "", 65.9, 300, QuestUnit.Seconds, 20, default, default, false)));
        Assert.Equal("45/200", QuestFormat.Progress(new QuestView("x", "c", QuestTier.Easy, "", "", 45, 200, QuestUnit.Count, 20, default, default, false)));
        Assert.Equal("1:02:03", QuestFormat.Remaining(TimeSpan.FromSeconds(3723)));
        Assert.Equal("0:00", QuestFormat.Remaining(TimeSpan.FromSeconds(-5)));
    }
}

public sealed class QuestBoardTests
{
    private static readonly GameInstall Minecraft = new("game:other:minecraft", "Minecraft", @"C:\Games\Minecraft");
    private static readonly GameInstall Doom = new("game:steam:2280", "DOOM", @"C:\Games\Doom");

    private readonly RecordingLog _log = new();
    private readonly EventBus _bus;
    private readonly MemoryStore _saves = new();
    private readonly CoinEconomy _economy;
    private readonly ManualClock _clock = new();
    private readonly TextCatalog _texts;
    private readonly List<QuestIssued> _issued = [];
    private readonly List<QuestCompleted> _completed = [];
    private readonly List<StatCounted> _counters = [];
    private readonly List<HudStatusChanged> _hud = [];
    private IReadOnlyList<QuestView> _quests = [];

    public QuestBoardTests()
    {
        _bus = new EventBus(_log);
        _economy = new CoinEconomy(new MemoryStore(), _bus, _clock);
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        _texts = new TextCatalog(data, "ru", _log);
        _texts.Use("ru");
        _bus.Subscribe<QuestIssued>(_issued.Add);
        _bus.Subscribe<QuestCompleted>(_completed.Add);
        _bus.Subscribe<StatCounted>(_counters.Add);
        _bus.Subscribe<QuestsChanged>(change => _quests = change.Quests);
        _bus.Subscribe<HudStatusChanged>(_hud.Add);
    }

    private static QuestTemplate Clicks(string id, QuestTier tier = QuestTier.Easy, double goal = 3, string? button = null) =>
        new(id, tier, QuestTypes.Clicks, goal, 20, 20, "Сделай {goal} кликов", "cursor", Button: button);

    private QuestBoard? _board;

    private QuestBoard Board(QuestOptions? options = null, params QuestTemplate[] templates) =>
        _board = new(templates, options ?? QuestOptions.Default, _economy, _bus, _saves, _texts, _clock, new Random(7), _log);

    /// <summary>Задание прямо с доски: прогресс рассылается раз в такт, а проверять его нужно сразу.</summary>
    private QuestView Quest(QuestTier tier) => _board!.Snapshot().Single(quest => quest.Tier == tier);

    private static void Click(QuestBoard board, int count, MouseButton button = MouseButton.Left)
    {
        for (var index = 0; index < count; index++)
        {
            board.OnClick(new ClickCounted(button, TimeSpan.FromSeconds(index)));
        }
    }

    [Fact]
    public void Each_tier_refreshes_on_its_own_timer()
    {
        var board = Board(null, Clicks("e1"), Clicks("e2"), Clicks("m1", QuestTier.Medium), Clicks("m2", QuestTier.Medium), Clicks("h1", QuestTier.Hard), Clicks("h2", QuestTier.Hard));
        board.Tick();
        Assert.Equal(3, _issued.Count);
        Assert.Equal([QuestTier.Easy, QuestTier.Medium, QuestTier.Hard], _quests.Select(quest => quest.Tier));
        var first = _quests.ToDictionary(quest => quest.Tier, quest => quest.Id);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(15), Quest(QuestTier.Easy).Refresh);

        _clock.Advance(TimeSpan.FromMinutes(14));
        board.Tick();
        Assert.Equal(3, _issued.Count);

        _clock.Advance(TimeSpan.FromMinutes(1));
        board.Tick();
        Assert.Equal(4, _issued.Count);
        Assert.NotEqual(first[QuestTier.Easy], Quest(QuestTier.Easy).Id);
        Assert.Equal(first[QuestTier.Medium], Quest(QuestTier.Medium).Id);

        _clock.Advance(TimeSpan.FromMinutes(15));
        board.Tick();
        Assert.NotEqual(first[QuestTier.Medium], Quest(QuestTier.Medium).Id);
        Assert.Equal(first[QuestTier.Hard], Quest(QuestTier.Hard).Id);

        _clock.Advance(TimeSpan.FromMinutes(30));
        board.Tick();
        Assert.NotEqual(first[QuestTier.Hard], Quest(QuestTier.Hard).Id);
        Assert.Equal(9, _issued.Count);
        Assert.All(_issued, issued => Assert.True(issued.Notify));
    }

    [Fact]
    public void Unfinished_quest_is_replaced_and_the_same_template_does_not_repeat()
    {
        var board = Board(null, Clicks("a"), Clicks("b"));
        var templates = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            board.Tick();
            templates.Add(Quest(QuestTier.Easy).Template);
            Click(board, 1);
            _clock.Advance(TimeSpan.FromMinutes(15));
        }

        for (var index = 1; index < templates.Count; index++)
        {
            Assert.NotEqual(templates[index - 1], templates[index]);
        }

        Assert.Empty(_completed);
        Assert.Equal(0, _economy.Balance);
    }

    [Fact]
    public void Intervals_come_from_settings()
    {
        var settings = new MemoryStore();
        settings.Set("interval/easy", 5.0);
        settings.Set(QuestsModule.NotifySetting, false);
        settings.Set(QuestsModule.DisabledTypesSetting, new[] { QuestTypes.Spend });
        var options = QuestsModule.ReadOptions(settings);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Intervals[QuestTier.Easy]);
        Assert.Equal(TimeSpan.FromMinutes(30), options.Intervals[QuestTier.Medium]);
        Assert.False(options.Notify);

        // Прогрев модуля ждёт игры и покупки от других модулей, задания выдаются после него.
        Assert.Equal(TimeSpan.FromSeconds(5), options.Warmup);
        var warming = Board(options, Clicks("a"));
        warming.Tick();
        Assert.Empty(warming.Snapshot());

        var board = Board(options with { Warmup = TimeSpan.Zero }, Clicks("a"), new QuestTemplate("s", QuestTier.Medium, QuestTypes.Spend, 10, 75, 75, "Потрать", "coin"));
        board.Tick();
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(5), Quest(QuestTier.Easy).Refresh);
        Assert.DoesNotContain(_quests, quest => quest.Tier == QuestTier.Medium);
        Assert.All(_issued, issued => Assert.False(issued.Notify));
    }

    [Fact]
    public void Reward_arrives_exactly_once_and_survives_a_restart()
    {
        var board = Board(null, Clicks("a", goal: 3));
        board.Tick();
        Click(board, 2);
        Assert.Empty(_completed);
        Click(board, 5);

        var completed = Assert.Single(_completed);
        Assert.Equal(20, completed.Earned);
        Assert.Equal(20, _economy.Balance);
        Assert.True(Quest(QuestTier.Easy).Completed);
        Assert.Equal(1, Quest(QuestTier.Easy).Share);
        Assert.Single(_counters, counter => counter.Key == "quests.completed.easy");
        Assert.Contains(_counters, counter => counter.Key == "quests.coins" && counter.Amount == 20);

        // Перезапуск: выполненное задание остаётся выполненным и не платит снова.
        var restarted = Board(null, Clicks("a", goal: 3));
        restarted.Tick();
        Click(restarted, 10);
        Assert.Single(_completed);
        Assert.Equal(20, _economy.Balance);
        Assert.True(Assert.Single(restarted.Snapshot()).Completed);
    }

    [Fact]
    public void Reward_is_paid_once_when_clicks_race_from_many_threads()
    {
        var board = Board(null, Clicks("a", goal: 50));
        board.Tick();
        Parallel.For(0, 400, index => board.OnClick(new ClickCounted(MouseButton.Left, TimeSpan.FromMilliseconds(index))));
        Assert.Single(_completed);
        Assert.Equal(20, _economy.Balance);
    }

    [Fact]
    public void Saved_progress_continues_and_expired_quest_is_replaced_after_a_restart()
    {
        var board = Board(null, Clicks("a", goal: 10), Clicks("b", goal: 10));
        board.Tick();
        Click(board, 4);
        board.Tick();
        var id = Quest(QuestTier.Easy).Id;

        var restarted = Board(null, Clicks("a", goal: 10), Clicks("b", goal: 10));
        restarted.Publish();
        Assert.Equal(id, Quest(QuestTier.Easy).Id);
        Assert.Equal(4, Quest(QuestTier.Easy).Progress);

        _clock.Advance(TimeSpan.FromHours(3));
        var later = Board(null, Clicks("a", goal: 10), Clicks("b", goal: 10));
        later.Tick();
        Assert.NotEqual(id, Quest(QuestTier.Easy).Id);
        Assert.Equal(0, Quest(QuestTier.Easy).Progress);
    }

    [Fact]
    public void Clicks_of_other_buttons_do_not_count()
    {
        var board = Board(null, Clicks("r", goal: 2, button: "right"));
        board.Tick();
        Click(board, 5, MouseButton.Left);
        Assert.Equal(0, Quest(QuestTier.Easy).Progress);
        Click(board, 2, MouseButton.Right);
        Assert.Single(_completed);
    }

    [Fact]
    public void Game_quest_needs_an_installed_and_bought_game()
    {
        var game = new QuestTemplate("g", QuestTier.Hard, QuestTypes.Game, 10, 300, 300, "Поиграй в {game} {goal} минут", "trophy");
        var anyGame = new QuestTemplate("any", QuestTier.Medium, QuestTypes.Media, 5, 100, 100, "Поиграй {goal} минут", "trophy", Source: "game");
        var board = Board(null, game, anyGame);

        // Игры неизвестны: задание на отсутствующую игру не выдаётся.
        board.Tick();
        Assert.Empty(_quests);

        // Игра установлена, но не куплена: заблокированные игры в задания не попадают.
        board.OnGames([Minecraft, Doom]);
        board.OnCollection(new CollectionChanged([], [new CollectionCatalog(LockKind.Game, 2, 0)]));
        _clock.Advance(TimeSpan.FromMinutes(1));
        board.Tick();
        Assert.Empty(_quests);

        board.OnCollection(new CollectionChanged(
            [new CollectionItem(Minecraft.Key, "Minecraft", LockKind.Game, 500, _clock.GetUtcNow())],
            [new CollectionCatalog(LockKind.Game, 2, 1)]));
        _clock.Advance(TimeSpan.FromMinutes(1));
        board.Tick();
        Assert.Equal("Поиграй в Minecraft 10 минут", Quest(QuestTier.Hard).Title);
        Assert.Equal(600, Quest(QuestTier.Hard).Goal);
        Assert.Equal([Minecraft], board.PlayableGames());

        // Время в другой игре засчитывается только заданию «во что угодно».
        board.OnMedia(new MediaTimeCounted("game", "DOOM", 120));
        Assert.Equal(0, Quest(QuestTier.Hard).Progress);
        Assert.Equal(120, Quest(QuestTier.Medium).Progress);
        board.OnMedia(new MediaTimeCounted("video", "Minecraft", 120));
        Assert.Equal(0, Quest(QuestTier.Hard).Progress);
        board.OnMedia(new MediaTimeCounted("game", "Minecraft", 599));
        Assert.Equal(599, Quest(QuestTier.Hard).Progress);
        Assert.True(Quest(QuestTier.Medium).Completed);
        board.OnMedia(new MediaTimeCounted("game", "Minecraft", 1));
        Assert.True(Quest(QuestTier.Hard).Completed);
        Assert.Equal(400, _economy.Balance);
    }

    [Fact]
    public void Quest_on_an_uninstalled_game_is_replaced_at_once()
    {
        var game = new QuestTemplate("g", QuestTier.Hard, QuestTypes.Game, 10, 300, 300, "Поиграй в {game}", "trophy");
        var board = Board(null, game, Clicks("c", QuestTier.Hard));
        board.OnGames([Minecraft]);
        board.OnCollection(new CollectionChanged([new CollectionItem(Minecraft.Key, "Minecraft", LockKind.Game, 500, _clock.GetUtcNow())], []));

        // Шаблон с игрой выбирается, пока не выпадет: случайность у теста своя, поэтому достаточно нескольких попыток.
        for (var attempt = 0; attempt < 20 && _quests.FirstOrDefault()?.Template != "g"; attempt++)
        {
            board.RefreshAll();
            board.Tick();
        }

        Assert.Equal("g", Quest(QuestTier.Hard).Template);
        var refresh = Quest(QuestTier.Hard).Refresh;
        board.OnGames([]);
        Assert.Equal("c", Quest(QuestTier.Hard).Template);
        Assert.Equal(refresh, Quest(QuestTier.Hard).Refresh);
    }

    [Fact]
    public void Purchase_quests_count_spending_upgrades_and_unlocks_only_when_possible()
    {
        var upgrade = new QuestTemplate("u", QuestTier.Medium, QuestTypes.Upgrade, 1, 75, 75, "Купи улучшение", "arrow_up");
        var unlock = new QuestTemplate("k", QuestTier.Hard, QuestTypes.Unlock, 1, 200, 200, "Открой сайт", "unlock", Kind: "site");
        var spend = new QuestTemplate("s", QuestTier.Easy, QuestTypes.Spend, 100, 20, 20, "Потрать {goal} монеток", "coin");
        var board = Board(null, upgrade, unlock, spend);

        // Магазин и каталог неизвестны: покупать нечего.
        board.Tick();
        Assert.Equal([QuestTier.Easy], _quests.Select(quest => quest.Tier));

        var offer = new UpgradeOffer(new UpgradeDefinition("x", "b", "X", "", "cursor", "income.bonus", new Dictionary<string, System.Text.Json.JsonElement>(), 10, 1.5m, 1, new Dictionary<string, int>()), 0, 10, []);
        board.OnShop(new UpgradeShopChanged([new UpgradeShopBranch("b", "B", "cursor", [offer with { NextPrice = null, Level = 1 }])]));
        board.OnCollection(new CollectionChanged([], [new CollectionCatalog(LockKind.Site, 3, 3), new CollectionCatalog(LockKind.Program, 5, 0)]));
        _clock.Advance(TimeSpan.FromMinutes(1));
        board.Tick();
        Assert.Equal([QuestTier.Easy], _quests.Select(quest => quest.Tier));

        board.OnShop(new UpgradeShopChanged([new UpgradeShopBranch("b", "B", "cursor", [offer])]));
        board.OnCollection(new CollectionChanged([], [new CollectionCatalog(LockKind.Site, 3, 2)]));
        _clock.Advance(TimeSpan.FromMinutes(1));
        board.Tick();
        Assert.Equal(3, _quests.Count);

        board.OnSpent(new CoinsSpent("upgrade:x", 60, 0));
        Assert.Equal(60, Quest(QuestTier.Easy).Progress);
        board.OnUpgrade(new UpgradePurchased("x", "X", 1, 60, _clock.GetUtcNow()));
        Assert.True(Quest(QuestTier.Medium).Completed);
        board.OnUnlock(new ItemUnlocked(new LockOffer("program:a", "A", 50, LockKind.Program), _clock.GetUtcNow()));
        Assert.False(Quest(QuestTier.Hard).Completed);
        board.OnUnlock(new ItemUnlocked(new LockOffer("site:a", "A", 50, LockKind.Site), _clock.GetUtcNow()));
        Assert.True(Quest(QuestTier.Hard).Completed);
        board.OnSpent(new CoinsSpent("unlock:x", 40, 0));
        Assert.True(Quest(QuestTier.Easy).Completed);
        Assert.Equal(3, _completed.Count);
    }

    [Fact]
    public void Hud_shows_the_first_unfinished_quest_with_progress()
    {
        var board = Board(null, Clicks("a", goal: 2), Clicks("m", QuestTier.Medium, goal: 100));
        board.Tick();
        Assert.Equal("Сделай 2 кликов 0/2", _hud[^1].Text);
        Assert.Equal(HudSlot.Quest, _hud[^1].Slot);

        Click(board, 1);
        board.Tick();
        Assert.Equal("Сделай 2 кликов 1/2", _hud[^1].Text);
        var count = _hud.Count;
        board.Tick();
        Assert.Equal(count, _hud.Count);

        Click(board, 1);
        // Клики шли и в среднее задание: любая кнопка подходит обоим.
        Assert.Equal("Сделай 100 кликов 2/100", _hud[^1].Text);
    }

    [Fact]
    public void No_feasible_templates_leave_the_tier_empty_and_retry_later()
    {
        var game = new QuestTemplate("g", QuestTier.Easy, QuestTypes.Game, 10, 30, 30, "Поиграй в {game}", "trophy");
        var board = Board(null, game);
        board.Tick();
        Assert.Empty(_quests);
        var warnings = _log.Warnings.Count;
        board.Tick();
        Assert.Equal(warnings, _log.Warnings.Count);

        board.OnGames([Minecraft]);
        board.OnCollection(new CollectionChanged([new CollectionItem(Minecraft.Key, "Minecraft", LockKind.Game, 500, _clock.GetUtcNow())], []));
        _clock.Advance(TimeSpan.FromMinutes(1));
        board.Tick();
        Assert.Equal("Поиграй в Minecraft", Quest(QuestTier.Easy).Title);
    }
}

/// <summary>Модуль заданий подключается из папки modules без правки ядра.</summary>
public sealed class QuestsModuleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Module_loads_from_the_modules_folder_and_answers_requests()
    {
        var modules = _temp.Combine("modules");
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Quests", modules);
        var bus = new EventBus(_log);
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        var store = new JsonFileStore(_temp.Combine("save.json"), _log);
        store.Load();
        var economy = new CoinEconomy(store, bus, TimeProvider.System);
        var texts = new TextCatalog(data, "ru", _log);
        texts.Use("ru");
        IReadOnlyList<QuestView>? quests = null;
        var requests = new List<string>();
        bus.Subscribe<QuestsChanged>(change => quests = change.Quests);
        bus.Subscribe<InstalledGamesRequested>(_ => requests.Add("games"));
        bus.Subscribe<CollectionRequested>(_ => requests.Add("collection"));
        bus.Subscribe<UpgradesRequested>(_ => requests.Add("upgrades"));

        var host = new ModuleHost(bus, data, texts, economy, store, store, new RecoveryService(new ChangeJournal(_temp.Combine("recovery.json"), _log), () => [], _log), _log, developerMode: false);
        host.Start(ModuleLoader.Load(modules, _log));
        try
        {
            Assert.Equal(["quests"], host.Running.Select(module => module.Id));
            Assert.Equal(["games", "collection", "upgrades"], requests);
            Assert.NotNull(quests);
            quests = null;
            bus.Publish(new QuestsRequested());
            Assert.NotNull(quests);
            QuestPagesChanged? pages = null;
            bus.Subscribe<QuestPagesChanged>(change => pages = change);
            bus.Publish(new QuestPagesRequested());
            Assert.NotNull(pages);
            Assert.Contains(_log.Infos, line => line.Contains("Шаблонов заданий: ", StringComparison.Ordinal));
            Assert.Empty(_log.Errors);
        }
        finally
        {
            host.StopAll();
        }
    }
}
