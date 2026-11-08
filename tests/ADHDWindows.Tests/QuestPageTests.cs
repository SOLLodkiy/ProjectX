using System.Net.Http;
using System.Text.Json;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.Stats;
using ADHDWindows.Modules.Quests;

namespace ADHDWindows.Tests;

/// <summary>Задания на страницы, проверка ссылок, пропуски и цепочки (этап 13).</summary>
public sealed class QuestPageTests : IDisposable
{
    private const string Cucumber = "https://ru.wikipedia.org/wiki/Огурец";
    private const string Track = "https://soundcloud.com/forss/flickermood";

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly EventBus _bus;
    private readonly MemoryStore _saves = new();
    private readonly CoinEconomy _economy;
    private readonly ManualClock _clock = new();
    private readonly TextCatalog _texts;
    private readonly StubLinks _links = new();
    private readonly List<QuestCompleted> _completed = [];
    private readonly List<QuestPagesChanged> _pages = [];
    private readonly List<QuestChainCompleted> _chains = [];
    private readonly List<StatCounted> _counters = [];
    private readonly List<FeedPosted> _feed = [];
    private QuestsChanged? _changed;
    private QuestBoard? _board;

    public QuestPageTests()
    {
        _bus = new EventBus(_log);
        _economy = new CoinEconomy(new MemoryStore(), _bus, _clock);
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        _texts = new TextCatalog(data, "ru", _log);
        _texts.Use("ru");
        _bus.Subscribe<QuestCompleted>(_completed.Add);
        _bus.Subscribe<QuestPagesChanged>(_pages.Add);
        _bus.Subscribe<QuestChainCompleted>(_chains.Add);
        _bus.Subscribe<StatCounted>(_counters.Add);
        _bus.Subscribe<FeedPosted>(_feed.Add);
        _bus.Subscribe<QuestsChanged>(change => _changed = change);
    }

    public void Dispose()
    {
        _board?.Dispose();
        _temp.Dispose();
    }

    private QuestBoard Board(params QuestTemplate[] templates)
    {
        _board?.Dispose();
        return _board = new QuestBoard(templates, QuestOptions.Default, _economy, _bus, _saves, _texts, _clock, new Random(3), _log, _links);
    }

    private QuestView Quest(QuestTier tier = QuestTier.Easy) => _board!.Snapshot().Single(quest => quest.Tier == tier);

    private static QuestTemplate Page(string id, string type, string url, QuestTier tier = QuestTier.Easy, string? word = null, double goal = 1) =>
        new(id, tier, type, goal, 30, 30, word is null ? "Задание" : "Выдели «{word}»", "quest", Url: url, Word: word);

    private static QuestTemplate Clicks(string id, QuestTier tier = QuestTier.Easy) =>
        new(id, tier, QuestTypes.Clicks, 1, 20, 20, "Клик", "cursor");

    private static BrowserPageReported Report(string url, PageActivityKind kind, bool focused = true, string? text = null, double? share = null) =>
        new(1, 5, PageAddress.Normalize(url)!, url, kind, focused, text, share);

    private static BrowserMediaReported Player(string url, double? position, bool playing = true, double rate = 1, bool audible = true, double duration = 200) =>
        new(1, null, [new BrowserTabMedia(5, url, false, [new BrowserMediaElement("audio", false, position, duration, playing, rate, audible, "Flickermood")])]);

    private void Click() => _board!.OnClick(new ClickCounted(MouseButton.Left, TimeSpan.Zero));

    // ---- Каталог ----

    [Fact]
    public void Page_templates_need_a_web_link_and_select_needs_a_word()
    {
        var path = _temp.Combine("quests", "pages.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "templates": [
              { "id": "visit", "tier": "easy", "type": "visit", "url": "https://ru.wikipedia.org/wiki/Арбуз", "reward": [20, 30], "title": "Открой" },
              { "id": "select", "tier": "medium", "type": "select", "url": "https://ru.wikipedia.org/wiki/Огурец", "word": "зелёный", "reward": [90, 90], "title": "Выдели «{word}»" },
              { "id": "listen", "tier": "medium", "type": "listen", "goal": 3.5, "url": "https://soundcloud.com/forss/flickermood", "reward": [90, 90], "title": "Слушай" },
              { "id": "no-url", "tier": "easy", "type": "visit", "reward": [20, 30], "title": "Открой" },
              { "id": "ftp", "tier": "easy", "type": "read", "url": "ftp://example.com/a", "reward": [20, 30], "title": "Читай" },
              { "id": "no-word", "tier": "easy", "type": "select", "url": "https://example.com", "reward": [20, 30], "title": "Выдели" },
              { "id": "url-on-clicks", "tier": "easy", "type": "clicks", "goal": 5, "url": "https://example.com", "reward": [20, 30], "title": "Клик" },
              { "id": "word-on-visit", "tier": "easy", "type": "visit", "url": "https://example.com", "reward": [20, 30], "title": "Найди {word}" },
              { "id": "listen-no-goal", "tier": "easy", "type": "listen", "url": "https://example.com", "reward": [20, 30], "title": "Слушай" }
            ] }
            """);
        var data = new GameData(_temp.Path, _log);
        data.Load();
        var templates = QuestCatalog.Load(data, _log);

        Assert.Equal(["visit", "select", "listen"], templates.Select(template => template.Id));
        Assert.Equal(6, _log.Errors.Count);
        Assert.Equal("ru.wikipedia.org/wiki/Огурец", templates[1].Page);
        Assert.Equal("зелёный", templates[1].Word);
        Assert.Equal(1, templates[0].GoalValue);
        Assert.Equal(QuestUnit.Seconds, templates[2].Unit);
        Assert.Equal(189, templates[2].GoalValue);
    }

    [Fact]
    public void Repository_pool_has_the_cucumber_and_soundcloud_examples()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        var templates = QuestCatalog.Load(data, _log);
        Assert.Empty(_log.Errors);

        var cucumber = Assert.Single(templates, template => template.Id == "medium.select.cucumber");
        Assert.Equal(QuestTypes.Select, cucumber.Type);
        // Не перенаправление: «Огурец» Википедия сама переводит на эту страницу, и пропуск на «Огурец» её бы закрыл.
        Assert.Equal("ru.wikipedia.org/wiki/Огурец_обыкновенный", cucumber.Page);
        Assert.Equal("зелёный", cucumber.Word);
        Assert.Contains(templates, template => template.Type == QuestTypes.Listen && template.Page!.StartsWith("soundcloud.com/", StringComparison.Ordinal));
        Assert.True(templates.Count(template => QuestTypes.Pages.Contains(template.Type)) >= 12);
        Assert.All(templates.Where(template => template.Type == QuestTypes.Select), template => Assert.Contains("{word}", template.Title));
    }

    [Fact]
    public void Word_is_put_into_the_title_and_the_link_into_the_view()
    {
        var board = Board(Page("s", QuestTypes.Select, Cucumber, word: "зелёный"));
        board.Tick();
        Assert.Equal("Выдели «зелёный»", Quest().Title);
        Assert.Equal(Cucumber, Quest().Url);
        Assert.Equal(Cucumber, _changed!.Quests.Single().Url);
    }

    // ---- Типы заданий ----

    [Fact]
    public void Visit_is_done_when_the_page_reports_anything()
    {
        var board = Board(Page("v", QuestTypes.Visit, Cucumber));
        board.Tick();
        board.OnPage(Report("https://ru.wikipedia.org/wiki/Огуречная_трава", PageActivityKind.Visit));
        Assert.False(Quest().Completed);

        board.OnPage(Report("https://ru.m.wikipedia.org/wiki/Огурец", PageActivityKind.Visit));
        Assert.False(Quest().Completed);

        board.OnPage(Report("https://ru.wikipedia.org/wiki/%D0%9E%D0%B3%D1%83%D1%80%D0%B5%D1%86#История", PageActivityKind.Visit, focused: false));
        Assert.True(Quest().Completed);
        Assert.Equal(30, _economy.Balance);
    }

    [Fact]
    public void Read_needs_the_end_of_the_page_in_a_focused_tab()
    {
        var board = Board(Page("r", QuestTypes.Read, Cucumber));
        board.Tick();
        board.OnPage(Report(Cucumber, PageActivityKind.Visit));
        board.OnPage(Report(Cucumber, PageActivityKind.Scroll, share: 0.5));
        board.OnPage(Report(Cucumber, PageActivityKind.Scroll, focused: false, share: 1));
        board.OnPage(Report(Cucumber, PageActivityKind.Selection, text: "огурец"));
        Assert.False(Quest().Completed);

        board.OnPage(Report(Cucumber, PageActivityKind.Scroll, share: 0.93));
        Assert.True(Quest().Completed);
        Assert.Single(_completed);
    }

    [Theory]
    [InlineData("зелёный", true)]
    [InlineData("  Зеленый, ", true)]
    [InlineData("«ЗЕЛЁНЫЙ»!", true)]
    [InlineData("зелёные", false)]
    [InlineData("изумрудно-зелёный", false)]
    [InlineData("", false)]
    public void Selection_ignores_case_yo_spaces_and_punctuation(string selected, bool counts)
    {
        var board = Board(Page("s", QuestTypes.Select, Cucumber, word: "Зелёный"));
        board.Tick();
        board.OnPage(Report(Cucumber, PageActivityKind.Selection, text: selected));
        Assert.Equal(counts, Quest().Completed);
    }

    [Fact]
    public void Selection_on_another_page_does_not_count()
    {
        var board = Board(Page("s", QuestTypes.Select, Cucumber, word: "зелёный"));
        board.Tick();
        board.OnPage(Report("https://ru.wikipedia.org/wiki/Трава", PageActivityKind.Selection, text: "зелёный"));
        Assert.False(Quest().Completed);
    }

    [Fact]
    public void Natural_listening_to_the_end_completes_the_track()
    {
        var board = Board(Page("l", QuestTypes.Listen, Track, goal: 3));
        board.Tick();
        Assert.Equal(162, Quest().Goal);

        var position = 0.0;
        board.OnBrowserMedia(Player(Track + "?in=forss/sets/soulhack", position));
        Assert.Equal(180, Quest().Goal);
        while (position < 178)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            position += 2;
            board.OnBrowserMedia(Player(Track + "?in=forss/sets/soulhack", position));
        }

        Assert.False(Quest().Completed);
        Assert.Equal(178, Quest().Progress, 3);
        _clock.Advance(TimeSpan.FromSeconds(2));
        board.OnBrowserMedia(Player(Track, position + 2));
        Assert.True(Quest().Completed);
        Assert.Equal(30, _economy.Balance);
    }

    [Fact]
    public void Seeking_forward_and_replaying_do_not_count()
    {
        var board = Board(Page("l", QuestTypes.Listen, Track, goal: 3));
        board.Tick();
        void Play(double from, double to, double step = 2)
        {
            for (var position = from; position <= to; position += step)
            {
                board.OnBrowserMedia(Player(Track, position));
                _clock.Advance(TimeSpan.FromSeconds(2));
            }
        }

        Play(0, 10);
        Assert.Equal(10, Quest().Progress, 3);

        // Перемотка вперёд: 2 секунды реального времени, а позиция выросла на 140.
        Play(150, 200);
        Assert.Equal(60, Quest().Progress, 3);

        // Повтор уже прослушанного куска не добавляет прогресса.
        Play(0, 10);
        Play(150, 200);
        Assert.Equal(60, Quest().Progress, 3);

        // Перемотка каждую секунду мелкими скачками тоже не засчитывается.
        for (var position = 20.0; position < 140; position += 10)
        {
            board.OnBrowserMedia(Player(Track, position));
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(60, Quest().Progress, 3);

        // Пауза, без звука и чужая вкладка не засчитываются.
        Play(20, 40, 2);
        var before = Quest().Progress;
        board.OnBrowserMedia(Player(Track, 40, playing: false));
        _clock.Advance(TimeSpan.FromSeconds(30));
        board.OnBrowserMedia(Player(Track, 70, playing: true));
        _clock.Advance(TimeSpan.FromSeconds(2));
        board.OnBrowserMedia(Player(Track, 72, audible: false));
        _clock.Advance(TimeSpan.FromSeconds(2));
        board.OnBrowserMedia(Player(Track, 74, audible: false));
        board.OnBrowserMedia(Player("https://soundcloud.com/forss/soulhack", 74));
        _clock.Advance(TimeSpan.FromSeconds(2));
        board.OnBrowserMedia(Player("https://soundcloud.com/forss/soulhack", 76));
        Assert.Equal(before, Quest().Progress, 3);
        Assert.False(Quest().Completed);
    }

    [Fact]
    public void Faster_playback_counts_at_its_rate_but_jumps_do_not()
    {
        var board = Board(Page("l", QuestTypes.Listen, Track, goal: 3));
        board.Tick();
        board.OnBrowserMedia(Player(Track, 0, rate: 2));
        _clock.Advance(TimeSpan.FromSeconds(2));
        board.OnBrowserMedia(Player(Track, 4, rate: 2));
        Assert.Equal(4, Quest().Progress, 3);

        _clock.Advance(TimeSpan.FromSeconds(2));
        board.OnBrowserMedia(Player(Track, 20, rate: 2));
        Assert.Equal(4, Quest().Progress, 3);
    }

    [Fact]
    public void Playback_faster_than_double_speed_does_not_count()
    {
        var board = Board(Page("l", QuestTypes.Listen, Track, goal: 3));
        board.Tick();
        board.OnBrowserMedia(Player(Track, 0, rate: 16));
        _clock.Advance(TimeSpan.FromSeconds(2));
        board.OnBrowserMedia(Player(Track, 32, rate: 16));
        Assert.Equal(0, Quest().Progress, 3);
    }

    [Fact]
    public void Listening_progress_survives_a_restart()
    {
        var template = Page("l", QuestTypes.Listen, Track, goal: 3);
        var board = Board(template);
        board.Tick();
        for (var position = 0; position <= 100; position += 2)
        {
            board.OnBrowserMedia(Player(Track, position));
            _clock.Advance(TimeSpan.FromSeconds(2));
        }

        board.Tick();
        var restarted = Board(template);
        Assert.Equal(100, Quest().Progress, 3);
        for (var position = 0; position <= 100; position += 2)
        {
            restarted.OnBrowserMedia(Player(Track, position));
            _clock.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(100, Quest().Progress, 3);
    }

    // ---- Проверка ссылок ----

    [Fact]
    public void Broken_link_is_not_issued_and_is_checked_again_later()
    {
        _links.Results[Cucumber] = false;
        var board = Board(Page("v", QuestTypes.Visit, Cucumber), Clicks("c"));
        board.Tick();
        Assert.Equal("c", Quest().Template);
        Assert.Equal(1, _links.Calls[Cucumber]);

        for (var round = 0; round < 5; round++)
        {
            board.RefreshAll();
            board.Tick();
            Assert.Equal("c", Quest().Template);
        }

        // Неудача помнится 30 минут, потом ссылка проверяется снова.
        Assert.Equal(1, _links.Calls[Cucumber]);
        _links.Results[Cucumber] = true;
        _clock.Advance(LinkStatus.BrokenFor);
        board.Tick();
        Assert.Equal(2, _links.Calls[Cucumber]);
        Assert.Equal("v", Quest().Template);
    }

    [Fact]
    public void Unchecked_link_is_issued_after_the_background_check()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _links.Pending[Cucumber] = pending;
        var board = Board(Page("v", QuestTypes.Visit, Cucumber));
        board.Tick();
        Assert.Empty(board.Snapshot());

        // Успешная проверка снимает минутное ожидание пустой сложности: часы стоят, а задание выдаётся.
        pending.SetResult(true);
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                board.Tick();
                return board.Snapshot().Count == 1;
            },
            TimeSpan.FromSeconds(5)));
        Assert.Equal("v", Quest().Template);
        Assert.Equal(1, _links.Calls[Cucumber]);

        // Удачная проверка помнится 6 часов.
        _clock.Advance(LinkStatus.OpenFor - TimeSpan.FromMinutes(1));
        board.Tick();
        Assert.Equal(1, _links.Calls[Cucumber]);
    }

    [Fact]
    public void Local_links_are_not_checked_over_the_network()
    {
        var board = Board(Page("v", QuestTypes.Visit, "http://127.0.0.1:8123/quest.html"), Page("w", QuestTypes.Visit, "http://localhost:8123/a", QuestTier.Medium));
        board.Tick();
        Assert.Equal(2, board.Snapshot().Count);
        Assert.Empty(_links.Calls);
    }

    [Fact]
    public void Http_checker_uses_a_generic_user_agent_and_treats_errors_as_closed()
    {
        Assert.DoesNotContain("@", HttpLinkChecker.UserAgent, StringComparison.Ordinal);
        Assert.True(LinkStatus.IsLocal("http://127.0.0.1:1/x"));
        Assert.True(LinkStatus.IsLocal("http://[::1]/"));
        Assert.False(LinkStatus.IsLocal(Cucumber));

        _links.Throw.Add(Cucumber);
        var board = Board(Page("v", QuestTypes.Visit, Cucumber));
        board.Tick();
        Assert.Empty(board.Snapshot());
        Assert.Contains(_log.Warnings, line => line.Contains(Cucumber, StringComparison.Ordinal));
    }

    // ---- Страницы и пропуски ----

    [Fact]
    public void Pages_and_passes_follow_the_current_quests()
    {
        var board = Board(
            Page("s", QuestTypes.Select, Cucumber, word: "зелёный"),
            Page("r", QuestTypes.Read, "https://ru.wikipedia.org/wiki/Луна", QuestTier.Medium),
            Clicks("c", QuestTier.Hard),
            Clicks("c2"));
        for (var attempt = 0; attempt < 20 && board.Snapshot().FirstOrDefault()?.Template != "s"; attempt++)
        {
            board.RefreshAll();
            board.Tick();
        }

        var pages = _pages[^1].Pages;
        Assert.Equal(2, pages.Count);
        var select = Assert.Single(pages, page => page.Url == Cucumber);
        Assert.Equal(new QuestPage(Quest().Id, "ru.wikipedia.org/wiki/Огурец", Cucumber, true, true, false), select);
        var read = Assert.Single(pages, page => page.Page == "ru.wikipedia.org/wiki/Луна");
        Assert.True(read is { Pass: true, WatchSelection: false, WatchScroll: true });

        // После выполнения пропуск остаётся до смены задания, а выделение больше не читается.
        board.OnPage(Report(Cucumber, PageActivityKind.Selection, text: "зелёный"));
        select = Assert.Single(_pages[^1].Pages, page => page.Url == Cucumber);
        Assert.True(select is { Pass: true, WatchSelection: false, WatchScroll: false });

        // Без изменений страницы не рассылаются повторно, но отвечают на запрос.
        var count = _pages.Count;
        board.Tick();
        Assert.Equal(count, _pages.Count);
        board.PublishPages();
        Assert.Equal(count + 1, _pages.Count);

        // Смена задания по таймеру снимает пропуск.
        _clock.Advance(TimeSpan.FromMinutes(15));
        board.Tick();
        Assert.DoesNotContain(_pages[^1].Pages, page => page.Url == Cucumber);
    }

    [Fact]
    public void Test_templates_replace_the_pool_only_in_developer_mode()
    {
        var templates = JsonDocument.Parse("""
            [ { "id": "local.select", "tier": "easy", "type": "select", "url": "http://127.0.0.1:8123/quest.html", "word": "зелёный", "reward": [20, 20], "title": "Выдели {word}" } ]
            """).RootElement;
        var settings = new MemoryStore();
        settings.Set(QuestsModule.TestTemplatesSetting, templates);
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();

        var loaded = QuestsModule.LoadTemplates(data, settings, developerMode: true, _log);
        Assert.Equal(["local.select"], loaded.Select(template => template.Id));
        Assert.Contains(_log.Infos, line => line.Contains("testTemplates", StringComparison.Ordinal));

        // Без режима разработчика настройка ничего не меняет.
        var normal = QuestsModule.LoadTemplates(data, settings, developerMode: false, _log);
        Assert.Contains(normal, template => template.Id == "medium.select.cucumber");
        Assert.DoesNotContain(normal, template => template.Id == "local.select");
    }

    // ---- Цепочки ----

    [Fact]
    public void Three_quests_in_a_row_close_a_chain()
    {
        var board = Board(Clicks("a"), Clicks("b"));
        for (var round = 0; round < 3; round++)
        {
            board.Tick();
            Assert.Equal(round, _changed!.Chain);
            Click();
            _clock.Advance(TimeSpan.FromMinutes(15));
        }

        var chain = Assert.Single(_chains);
        Assert.Equal(3, chain.Length);
        Assert.Single(_counters, counter => counter.Key == "quests.chains");
        Assert.Contains(_feed, post => post.Text == "Цепочка: 3 задания подряд!");
        Assert.Equal(0, board.Chain);
        Assert.Equal(0, _changed!.Chain);
        Assert.Equal(3, _changed.ChainGoal);
        Assert.Equal("Цепочки заданий", _texts.Get("stats.counter.quests.chains"));
    }

    [Fact]
    public void Quest_missed_by_the_timer_resets_the_chain()
    {
        var board = Board(Clicks("a"), Clicks("b"));
        board.Tick();
        Click();
        _clock.Advance(TimeSpan.FromMinutes(15));
        board.Tick();
        Click();
        Assert.Equal(2, board.Chain);

        // Следующее задание не выполнено и сменилось по таймеру.
        _clock.Advance(TimeSpan.FromMinutes(15));
        board.Tick();
        Assert.Equal(2, board.Chain);
        _clock.Advance(TimeSpan.FromMinutes(15));
        board.Tick();
        Assert.Equal(0, board.Chain);
        Assert.Empty(_chains);
    }

    [Fact]
    public void Replacement_of_an_unfeasible_quest_keeps_the_chain_and_the_chain_is_saved()
    {
        var minecraft = new GameInstall("game:other:minecraft", "Minecraft", @"C:\Games\Minecraft");
        var bought = new CollectionChanged([new CollectionItem(minecraft.Key, "Minecraft", LockKind.Game, 500, _clock.GetUtcNow())], []);
        var game = new QuestTemplate("g", QuestTier.Hard, QuestTypes.Game, 10, 300, 300, "Поиграй в {game}", "trophy");
        var board = Board(game, Clicks("c", QuestTier.Hard), Clicks("e1"), Clicks("e2"));
        board.OnGames([minecraft]);
        board.OnCollection(bought);
        for (var attempt = 0; attempt < 20 && board.Snapshot().FirstOrDefault(quest => quest.Tier == QuestTier.Hard)?.Template != "g"; attempt++)
        {
            board.RefreshAll();
            board.Tick();
        }

        Assert.Equal("g", Quest(QuestTier.Hard).Template);

        // Два лёгких задания подряд: цепочка 2.
        Click();
        _clock.Advance(TimeSpan.FromMinutes(15));
        board.Tick();
        Click();
        Assert.Equal(2, board.Chain);

        // Игру удалили: задание заменено как невыполнимое, цепочка не прервана.
        board.OnGames([]);
        Assert.Equal("c", Quest(QuestTier.Hard).Template);
        Assert.Equal(2, board.Chain);

        Click();
        Assert.Single(_chains);
        Assert.Equal(0, Board(game, Clicks("c", QuestTier.Hard)).Chain);
    }

    [Fact]
    public void Chain_counter_is_kept_after_a_restart()
    {
        var board = Board(Clicks("a"), Clicks("b"));
        board.Tick();
        Click();
        Assert.Equal(1, board.Chain);

        var restarted = Board(Clicks("a"), Clicks("b"));
        Assert.Equal(1, restarted.Chain);
        restarted.Publish();
        Assert.Equal(1, _changed!.Chain);
    }

    /// <summary>Проверка ссылок без сети: заданные ответы, отложенные ответы и ошибки.</summary>
    private sealed class StubLinks : ILinkChecker
    {
        public Dictionary<string, bool> Results { get; } = [];

        public Dictionary<string, TaskCompletionSource<bool>> Pending { get; } = [];

        public HashSet<string> Throw { get; } = [];

        public Dictionary<string, int> Calls { get; } = [];

        public Task<bool> IsOpenAsync(string url, CancellationToken cancellation)
        {
            lock (Calls)
            {
                Calls[url] = Calls.GetValueOrDefault(url) + 1;
            }

            if (Throw.Contains(url))
            {
                throw new HttpRequestException("нет сети");
            }

            return Pending.TryGetValue(url, out var pending) ? pending.Task : Task.FromResult(Results.GetValueOrDefault(url, true));
        }
    }
}
