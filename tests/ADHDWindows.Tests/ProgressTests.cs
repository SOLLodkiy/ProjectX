using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Progress;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Stats;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Progress;

namespace ADHDWindows.Tests;

/// <summary>Часы с местным временем, равным UTC: дни и часы в проверках не зависят от пояса компьютера.</summary>
internal sealed class LocalClock : TimeProvider
{
    // Понедельник.
    private DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now;

    public DateOnly Today => DateOnly.FromDateTime(_now.UtcDateTime);

    public void Advance(TimeSpan time) => _now += time;

    public void Set(DateTimeOffset when) => _now = when;

    /// <summary>Тот же час через <paramref name="days"/> дней.</summary>
    public void Days(int days) => _now = _now.AddDays(days);

    public void Hour(int hour) => _now = new DateTimeOffset(_now.Year, _now.Month, _now.Day, hour, 0, 0, TimeSpan.Zero);
}

public sealed class ProgressCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Repository_data_loads_cleanly_with_known_counters_and_sprites()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        var catalog = ProgressCatalog.Load(data, _log);
        Assert.Empty(_log.Errors);
        Assert.Empty(_log.Warnings);

        var sprites = File.ReadAllLines(Path.Combine(RepositoryRoot.Find(), "assets", "src", "sprites", "icons.txt"))
            .Where(line => line.StartsWith("sprite ", StringComparison.Ordinal))
            .Select(line => line.Split(' ')[1])
            .ToHashSet();
        Assert.True(catalog.Achievements.Count >= 50);
        Assert.Contains(catalog.Achievements, achievement => achievement.Hidden && achievement.HasHours);
        Assert.All(catalog.Achievements, achievement => Assert.Contains(achievement.Icon, sprites));
        Assert.All(catalog.WeeklyGoals, goal => Assert.Contains(goal.Icon, sprites));
        Assert.All(catalog.XpRules.Select(rule => rule.Metric).Concat(catalog.Achievements.Select(a => a.Metric)).Concat(catalog.WeeklyGoals.Select(g => g.Metric)),
            metric => Assert.True(ProgressMetrics.IsKnown(metric), metric));

        // Звания идеи 010: от Новичка до Легенды рабочего стола.
        Assert.Equal("Новичок", catalog.Ranks[0].Name);
        Assert.Equal(1, catalog.Ranks[0].Level);
        Assert.Equal("Легенда рабочего стола", catalog.Ranks[^1].Name);
        Assert.Contains(catalog.Ranks, rank => rank.Cosmetic is not null);
        Assert.True(catalog.WeeklyGoals.Count > catalog.WeeklyCount);
    }

    [Fact]
    public void New_achievement_is_one_data_record_and_broken_records_are_skipped()
    {
        var path = _temp.Combine("progress", "achievements-extra.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "achievements": [
              { "id": "new", "metric": "clicks", "goal": 3, "reward": 40, "icon": "star", "title": "Новое", "text": "Три клика" },
              { "id": "new", "metric": "clicks", "goal": 5, "reward": 40, "title": "Повтор" },
              { "id": "goal", "metric": "clicks", "goal": 0, "reward": 40, "title": "Без цели" },
              { "id": "hours", "metric": "clicks", "goal": 1, "reward": 40, "hours": [3, 3], "title": "Пустые часы" },
              { "id": "night", "metric": "clicks", "goal": 1, "reward": 0, "hours": [23, 2], "hidden": true, "title": "Ночь" },
              { "metric": "clicks", "goal": 1, "title": "Без кода" }
            ] }
            """);
        var data = new GameData(_temp.Path, _log);
        data.Load();

        var catalog = ProgressCatalog.Load(data, _log);

        Assert.Equal(["new", "night"], catalog.Achievements.Select(achievement => achievement.Id));
        Assert.Equal(4, _log.Errors.Count(error => !error.Contains("нет", StringComparison.Ordinal)));
        var night = catalog.Achievements[1];
        Assert.True(night.InHours(23));
        Assert.True(night.InHours(1));
        Assert.False(night.InHours(2));
        Assert.False(night.InHours(12));
        Assert.Equal(10, night.Xp);
    }

    [Fact]
    public void Levels_need_more_experience_each_time()
    {
        var rules = new LevelRules(100, 50, 50, 20, 5);
        Assert.Equal((1, 99L, 100L), rules.Level(99));
        Assert.Equal((2, 0L, 150L), rules.Level(100));
        Assert.Equal((3, 10L, 200L), rules.Level(260));
        Assert.Equal((5, 1000L, 0L), rules.Level(100 + 150 + 200 + 250 + 1000));
        Assert.Equal(50, rules.Coins(2));
        Assert.Equal(90, rules.Coins(4));
    }
}

public sealed class ProgressTrackerTests
{
    private static readonly RankDefinition[] Ranks =
    [
        new("novice", 1, "Новичок", 0, null),
        new("clicker", 3, "Кликер", 100, "rank.clicker"),
    ];

    private readonly LocalClock _clock = new();
    private readonly RecordingLog _log = new();
    private readonly EventBus _bus;
    private readonly MemoryStore _saves = new();
    private readonly MemoryStore _settings = new();
    private readonly CoinEconomy _economy;
    private readonly List<object> _events = [];
    private ProgressCatalog _catalog;
    private ProgressTracker _tracker = null!;
    private IDisposable? _earning;
    private IDisposable? _spending;
    private double _time;

    public ProgressTrackerTests()
    {
        _bus = new EventBus(_log);
        _economy = new CoinEconomy(new MemoryStore(), _bus, _clock);
        _bus.Subscribe<LevelReached>(_events.Add);
        _bus.Subscribe<AchievementUnlocked>(_events.Add);
        _bus.Subscribe<StreakRewarded>(_events.Add);
        _bus.Subscribe<WeeklyGoalCompleted>(_events.Add);
        _bus.Subscribe<DaySummaryReady>(_events.Add);
        _catalog = Catalog();
    }

    private static ProgressCatalog Catalog(
        IReadOnlyList<AchievementDefinition>? achievements = null,
        IReadOnlyList<WeeklyGoalDefinition>? weekly = null,
        int weeklyCount = 2) => new(
        new LevelRules(10, 10, 50, 20, 50),
        Ranks,
        [new XpRule(ProgressMetrics.Clicks, 2, 1)],
        achievements ?? [],
        new StreakRules(20, 10, 60, true),
        weeklyCount,
        weekly ?? []);

    private static AchievementDefinition Achievement(string id, string metric, double goal, long reward = 30, bool hidden = false, int? from = null, int? to = null) =>
        new(id, metric, goal, reward, 15, "star", $"Достижение {id}", "Описание", hidden, from, to);

    private void Start()
    {
        _tracker = new ProgressTracker(_catalog, _economy, _bus, _saves, _settings, _clock, new Random(1), _log);
        _earning = _bus.Subscribe<CoinsEarned>(_tracker.OnEarned);
        _spending = _bus.Subscribe<CoinsSpent>(_tracker.OnSpent);
        _tracker.Start();
    }

    /// <summary>Перезапуск программы: новый трекер на том же сохранении.</summary>
    private void Restart()
    {
        _tracker.Save();
        _earning?.Dispose();
        _spending?.Dispose();
        _tracker = new ProgressTracker(_catalog, _economy, _bus, _saves, _settings, _clock, new Random(2), _log);
        _earning = _bus.Subscribe<CoinsEarned>(_tracker.OnEarned);
        _spending = _bus.Subscribe<CoinsSpent>(_tracker.OnSpent);
        _tracker.Start();
    }

    private void Click(int count = 1, MouseButton button = MouseButton.Left)
    {
        for (var index = 0; index < count; index++)
        {
            _time += 300;
            _tracker.OnClick(new ClickCounted(button, TimeSpan.FromMilliseconds(_time)));
        }
    }

    private List<T> Events<T>() => [.. _events.OfType<T>()];

    [Fact]
    public void Achievement_from_data_unlocks_once_with_the_reward_through_the_income_formula()
    {
        _catalog = Catalog([Achievement("three", ProgressMetrics.Clicks, 3, reward: 40)]);
        using var doubled = _economy.AddModifier(new IncomeModifier(IncomeStep.Upgrade, 2, ProgressTracker.AchievementSource));
        Start();
        var before = _economy.Balance;

        Click(2);
        Assert.Empty(Events<AchievementUnlocked>());
        Click(1);
        Click(10);

        var unlocked = Assert.Single(Events<AchievementUnlocked>());
        Assert.Equal("three", unlocked.Achievement.Id);
        Assert.Equal(80, unlocked.Earned);
        Assert.True(unlocked.Achievement.IsUnlocked);
        Assert.Equal(_clock.GetUtcNow(), unlocked.Achievement.Unlocked);

        // Награда не считается заработком: иначе достижения за монетки давали бы опыт и монетки по кругу.
        Assert.Equal(0, _tracker.Value(ProgressMetrics.Earned));
        Restart();
        Click(5);
        Assert.Single(Events<AchievementUnlocked>());
        Assert.True(_tracker.Snapshot().Achievements.Single().IsUnlocked);
        Assert.True(_economy.Balance - before >= 80);
    }

    [Fact]
    public void Hidden_achievement_stays_secret_and_counts_only_its_hours()
    {
        _catalog = Catalog([Achievement("night", ProgressMetrics.ProgramPrefix + "notepad", 1, hidden: true, from: 3, to: 4)]);
        _clock.Hour(2);
        Start();

        var secret = _tracker.Snapshot().Achievements.Single();
        Assert.Equal(string.Empty, secret.Title);
        Assert.Equal("lock", secret.Icon);

        _tracker.OnProgram("NOTEPAD");
        _clock.Hour(4);
        _tracker.OnProgram("notepad");
        Assert.Empty(Events<AchievementUnlocked>());
        Assert.Equal(2, _tracker.Value("program.notepad"));

        _clock.Days(1);
        _clock.Hour(3);
        _tracker.OnProgram("notepad");
        var unlocked = Assert.Single(Events<AchievementUnlocked>());
        Assert.Equal("Достижение night", unlocked.Achievement.Title);
        Assert.Equal("star", unlocked.Achievement.Icon);
    }

    [Fact]
    public void Experience_gives_levels_and_ranks_once_and_achievements_can_chain()
    {
        _catalog = Catalog([
            Achievement("level3", ProgressMetrics.Level, 3, reward: 0),
            Achievement("two", ProgressMetrics.Achievements, 1, reward: 0),
        ]);
        Start();
        Assert.Equal(1, _tracker.Snapshot().Level);

        // Уровень 2 — 10 опыта (20 кликов), уровень 3 — ещё 20 (40 кликов), уровень 4 — ещё 30. Опыт за достижения по 15.
        Click(20);
        var second = Assert.Single(Events<LevelReached>());
        Assert.Equal(2, second.Level);
        Assert.False(second.RankChanged);
        Assert.Equal(50, second.Earned);

        Click(40);
        var levels = Events<LevelReached>();
        var third = levels.Single(level => level.Level == 3);
        Assert.True(third.RankChanged);
        Assert.Equal("Кликер", third.RankName);
        Assert.Equal("rank.clicker", third.Cosmetic);
        Assert.Equal(70 + 100, third.Earned);

        // Уровень 3 даёт достижение, достижение — следующее, их опыт поднимает уровень дальше.
        Assert.Equal(["level3", "two"], Events<AchievementUnlocked>().Select(unlocked => unlocked.Achievement.Id));
        var snapshot = _tracker.Snapshot();
        Assert.Equal(30 + 30, snapshot.TotalXp);
        Assert.Equal(4, snapshot.Level);
        Assert.Equal(4, Events<LevelReached>().Max(level => level.Level));

        Restart();
        Assert.Equal(3, Events<LevelReached>().Count);
        Assert.Equal(2, Events<AchievementUnlocked>().Count);
    }

    [Fact]
    public void Streak_grows_daily_survives_restarts_and_one_skipped_day_a_week()
    {
        Start();
        var first = Assert.Single(Events<StreakRewarded>());
        Assert.Equal((1, 20L, false), (first.Days, first.Earned, first.SkipUsed));

        Restart();
        _clock.Advance(TimeSpan.FromHours(5));
        _tracker.Tick();
        Assert.Single(Events<StreakRewarded>());

        // Программа открыта через полночь: вход нового дня засчитывается сам.
        _clock.Hour(23);
        _clock.Advance(TimeSpan.FromHours(2));
        _tracker.Tick();
        Assert.Equal((2, 30L), (Events<StreakRewarded>()[^1].Days, Events<StreakRewarded>()[^1].Earned));

        // Среда пропущена: бесплатный пропуск недели.
        _clock.Days(2);
        Restart();
        var skipped = Events<StreakRewarded>()[^1];
        Assert.Equal(3, skipped.Days);
        Assert.True(skipped.SkipUsed);
        Assert.False(_tracker.Snapshot().SkipAvailable);

        // Второй пропуск на той же неделе рвёт серию.
        _clock.Days(2);
        Restart();
        var broken = Events<StreakRewarded>()[^1];
        Assert.Equal(1, broken.Days);
        Assert.True(broken.Broken);
        Assert.Equal(3, _tracker.Snapshot().BestStreak);

        // На следующей неделе пропуск снова доступен.
        _clock.Days(1);
        _tracker.Tick();
        _clock.Days(2);
        _tracker.Tick();
        Assert.Equal((3, true), (Events<StreakRewarded>()[^1].Days, Events<StreakRewarded>()[^1].SkipUsed));

        // Два пропущенных дня подряд рвут серию даже с пропуском.
        _clock.Days(3);
        _tracker.Tick();
        Assert.Equal(1, Events<StreakRewarded>()[^1].Days);
    }

    [Fact]
    public void Streak_reward_grows_up_to_the_limit_and_clock_going_back_changes_nothing()
    {
        Start();
        for (var day = 0; day < 6; day++)
        {
            _clock.Days(1);
            _tracker.Tick();
        }

        Assert.Equal([20L, 30, 40, 50, 60, 60, 60], Events<StreakRewarded>().Select(rewarded => rewarded.Earned));
        var count = _events.Count;

        // Часы переведены на два дня назад и обратно: серия не растёт и не рвётся, итог дня не появляется.
        _clock.Days(-2);
        Restart();
        _clock.Days(2);
        _tracker.Tick();
        Assert.Equal(count, _events.Count);
        Assert.Equal(7, _tracker.Snapshot().Streak);
    }

    [Fact]
    public void Day_summary_reports_the_finished_day_and_respects_the_setting()
    {
        Start();
        Click(30);
        Click(5, MouseButton.Right);
        _economy.Earn("click", 35);
        _economy.Earn("video", 100);
        _economy.TrySpend(20, "unlock:test");
        _tracker.OnMedia(new MediaTimeCounted("video", "youtube.com", 600));
        var today = _tracker.Snapshot().Today;
        Assert.Equal(35, today.Clicks);

        _clock.Days(1);
        _tracker.Tick();
        var ready = Assert.Single(Events<DaySummaryReady>());
        Assert.True(ready.Show);
        var summary = ready.Summary;
        Assert.Equal(_clock.Today.AddDays(-1), summary.Date);
        Assert.Equal(35, summary.Clicks);
        Assert.Equal(600, summary.MediaSeconds, 6);
        Assert.Equal(20, summary.Spent);
        Assert.Equal("video", summary.BestSource);
        Assert.Equal(100, summary.BestSourceEarned);
        Assert.True(summary.Record);
        Assert.True(summary.Earned >= 135);
        Assert.True(summary.Xp >= 17);
        Assert.Equal(summary, _tracker.Snapshot().LastSummary);

        // Пустой день итога не даёт, а выключенная настройка прячет окно, но итог всё равно считается.
        _clock.Days(1);
        _tracker.Tick();
        Assert.Single(Events<DaySummaryReady>());
        _tracker.SetSetting(ProgressSettings.DaySummary, false);
        _economy.Earn("click", 10);
        _clock.Days(1);
        _tracker.Tick();
        var hidden = Events<DaySummaryReady>()[^1];
        Assert.False(hidden.Show);
        Assert.False(hidden.Summary.Record);
    }

    [Fact]
    public void Weekly_goals_count_from_issue_reward_once_and_change_on_monday()
    {
        WeeklyGoalDefinition[] pool =
        [
            new("clicks", ProgressMetrics.Clicks, 10, 500, 0, "cursor", "Клики"),
            new("spent", ProgressMetrics.Spent, 1000, 500, 0, "shop", "Траты"),
            new("days", ProgressMetrics.DaysActive, 3, 500, 0, "flame", "Дни"),
        ];
        _catalog = Catalog(weekly: pool, weeklyCount: 3);
        _clock.Days(-1);
        Start();
        Click(50);
        _clock.Days(1);
        _tracker.Tick();
        _events.Clear();

        var snapshot = _tracker.Snapshot();
        Assert.Equal(3, snapshot.WeeklyGoals.Count);
        Assert.Equal(_clock.Today, snapshot.WeekStart);
        var clicks = snapshot.WeeklyGoals.Single(goal => goal.Id == "clicks");
        Assert.Equal(0, clicks.Progress);

        Click(9);
        Assert.Empty(Events<WeeklyGoalCompleted>());
        Click(1);
        Click(20);
        var completed = Assert.Single(Events<WeeklyGoalCompleted>());
        Assert.Equal(("clicks", 500L), (completed.Goal.Id, completed.Earned));

        // Дни недели: понедельник засчитан, нужны ещё два дня.
        _clock.Days(1);
        _tracker.Tick();
        _clock.Days(1);
        _tracker.Tick();
        Assert.Equal(2, Events<WeeklyGoalCompleted>().Count);

        _clock.Days(5);
        _tracker.Tick();
        var next = _tracker.Snapshot();
        Assert.Equal(_clock.Today, next.WeekStart);
        Assert.All(next.WeeklyGoals, goal => Assert.False(goal.Completed));
        Assert.All(next.WeeklyGoals, goal => Assert.Equal(goal.Id == "days" ? 1 : 0, goal.Progress));
    }

    [Fact]
    public void Notifications_switch_off_but_progress_still_counts()
    {
        _catalog = Catalog([Achievement("one", ProgressMetrics.Clicks, 1)]);
        Start();
        _tracker.SetSetting(ProgressSettings.Notifications, false);
        Click(1);

        var unlocked = Assert.Single(Events<AchievementUnlocked>());
        Assert.False(unlocked.Notify);
        Assert.Equal(30, unlocked.Earned);
        Assert.False(_tracker.Snapshot().Notifications);
    }

    [Fact]
    public void Counters_follow_game_events()
    {
        _catalog = Catalog();
        Start();
        Click(2, MouseButton.Middle);
        _tracker.OnUnlock(new ItemUnlocked(new LockOffer("site:a", "A", 10, LockKind.Site), _clock.GetUtcNow()));
        _tracker.OnCounter(new StatCounted("quests.completed.hard"));
        _tracker.OnHardshipEnded(new HardshipEnded(new HardshipInfo("gravity", HardshipLevel.Hard, HardshipResources.None), HardshipEndReason.Survived, 10));
        _tracker.OnHardshipEnded(new HardshipEnded(new HardshipInfo("gravity", HardshipLevel.Hard, HardshipResources.None), HardshipEndReason.Cancelled, 0));
        _tracker.OnProgram("Code");
        _tracker.OnProgram("code");

        Assert.Equal(2, _tracker.Value("clicks.middle"));
        Assert.Equal(1, _tracker.Value("unlocks.site"));
        Assert.Equal(1, _tracker.Value("quests.completed.hard"));
        Assert.Equal(1, _tracker.Value("hardships.survived.hard"));
        Assert.Equal(1, _tracker.Value(ProgressMetrics.ProgramsSeen));
        Assert.Equal(1, _tracker.Value(ProgressMetrics.DaysActive));
        Assert.Equal(1, _tracker.Value(ProgressMetrics.Launches));
    }
}

public sealed class ProgressModuleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly string _modules;
    private EventBus _bus = null!;
    private CoinEconomy _economy = null!;
    private JsonFileStore _store = null!;
    private ModuleHost _host = null!;
    private ProgressSnapshot? _snapshot;
    private readonly List<StreakRewarded> _streaks = [];
    private double _time;

    public ProgressModuleTests()
    {
        _modules = _temp.Combine("modules");
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Clicks", _modules);
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Progress", _modules);
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
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        _store = new JsonFileStore(_temp.Combine("save.json"), _log);
        _store.Load();
        _economy = new CoinEconomy(_store, _bus, TimeProvider.System);
        _economy.Load();
        _bus.Subscribe<ProgressChanged>(change => _snapshot = change.Snapshot);
        _bus.Subscribe<StreakRewarded>(_streaks.Add);
        var texts = new TextCatalog(data, "ru", _log);
        texts.Use("ru");
        _host = new ModuleHost(_bus, data, texts, _economy, _store, _store, new RecoveryService(new ChangeJournal(_temp.Combine("recovery.json"), _log), () => [], _log), _log, developerMode: false);
        _host.Start(ModuleLoader.Load(_modules, _log));
    }

    private ProgressSnapshot Request()
    {
        _snapshot = null;
        _bus.Publish(new ProgressRequested());
        return _snapshot ?? throw new InvalidOperationException("Прогресс не ответил");
    }

    private void Click()
    {
        _time += 300 + (_time % 97);
        _bus.Publish(new MouseButtonChanged(MouseButton.Left, true, false, TimeSpan.FromMilliseconds(_time)));
        _bus.Publish(new MouseButtonChanged(MouseButton.Left, false, false, TimeSpan.FromMilliseconds(_time + 40)));
    }

    [Fact]
    public void Module_loads_from_the_folder_counts_clicks_and_keeps_progress_after_a_restart()
    {
        Assert.Equal(["clicks", "progress"], _host.Running.Select(module => module.Id).Order());
        var streak = Assert.Single(_streaks);
        Assert.Equal(1, streak.Days);
        Assert.True(streak.Earned > 0);

        for (var index = 0; index < 120; index++)
        {
            Click();
        }

        var snapshot = Request();
        Assert.Contains(snapshot.Achievements, achievement => achievement.Id == "clicks.100" && achievement.IsUnlocked);
        Assert.Equal(120, snapshot.Today.Clicks);
        Assert.True(snapshot.TotalXp > 0);
        Assert.Equal(3, snapshot.WeeklyGoals.Count);

        _host.StopAll();
        _store.Flush();
        _streaks.Clear();
        Start();

        var restarted = Request();
        Assert.Empty(_streaks);
        Assert.Equal(snapshot.TotalXp, restarted.TotalXp);
        Assert.Equal(snapshot.WeeklyGoals.Select(goal => goal.Id), restarted.WeeklyGoals.Select(goal => goal.Id));
        Assert.Contains(restarted.Achievements, achievement => achievement.Id == "clicks.100" && achievement.IsUnlocked);
        Assert.Empty(_log.Errors);
    }
}
