
using System.Text.Json;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Stats;
using ADHDWindows.Core.Storage;
using ADHDWindows.Core.Upgrades;
using ADHDWindows.Modules.Locks;
using ADHDWindows.Modules.Locks.Sites;
using ADHDWindows.Modules.Stats;
using ADHDWindows.Modules.Time;

namespace ADHDWindows.Tests;

public sealed class StatsRecorderTests
{
    private readonly ManualClock _clock = new();
    private readonly MemoryStore _store = new();
    private readonly StatsRecorder _recorder;

    public StatsRecorderTests() => _recorder = new StatsRecorder(_store, _clock);

    private static DateOnly LocalDate(DateTimeOffset when) => DateOnly.FromDateTime(when.ToLocalTime().DateTime);

    [Fact]
    public void Counters_match_the_actions()
    {
        _recorder.OnEarned(new CoinsEarned("click", 1, 1));
        _recorder.OnEarned(new CoinsEarned("click", 2, 3));
        _recorder.OnEarned(new CoinsEarned("video", 10, 13));
        _recorder.OnSpent(new CoinsSpent("unlock:site:youtube.com", 5, 8));
        for (var index = 0; index < 7; index++)
        {
            _recorder.OnClick(new ClickCounted(MouseButton.Left, TimeSpan.FromSeconds(index)));
        }

        _recorder.OnClick(new ClickCounted(MouseButton.Right, TimeSpan.FromSeconds(10)));
        _recorder.OnClick(new ClickCounted(MouseButton.Middle, TimeSpan.FromSeconds(11)));
        _recorder.AddCursor(StatsRecorder.PixelsPerMeter * 2.5);
        _recorder.AddAppTime(90);
        _recorder.OnCounter(new StatCounted("quests.completed.easy"));
        _recorder.OnCounter(new StatCounted("quests.completed.easy", 2));

        var snapshot = _recorder.Snapshot(balance: 8);

        Assert.Equal(8, snapshot.Balance);
        Assert.Equal(13, snapshot.TotalEarned);
        Assert.Equal(5, snapshot.TotalSpent);
        Assert.Equal(3, snapshot.EarnedBySource["click"]);
        Assert.Equal(10, snapshot.EarnedBySource["video"]);
        Assert.Equal(7, snapshot.Clicks["left"]);
        Assert.Equal(1, snapshot.Clicks["right"]);
        Assert.Equal(1, snapshot.Clicks["middle"]);
        Assert.Equal(9, snapshot.ClicksTotal);
        Assert.Equal(2.5, snapshot.CursorMeters, 6);
        Assert.Equal(90, snapshot.AppSeconds, 6);
        Assert.Equal(3, snapshot.Counters["quests.completed.easy"]);

        var today = Assert.Single(snapshot.Days);
        Assert.Equal(LocalDate(_clock.GetUtcNow()), today.Date);
        Assert.Equal(13, today.Earned);
        Assert.Equal(5, today.Spent);
        Assert.Equal(9, today.Clicks);
        Assert.Equal(13, snapshot.BestDay!.Earned);
    }

    [Fact]
    public void Clicks_per_second_record_counts_a_sliding_second()
    {
        // Шесть нажатий за 0,9 с, потом медленные.
        for (var index = 0; index < 6; index++)
        {
            _recorder.OnClick(new ClickCounted(MouseButton.Left, TimeSpan.FromMilliseconds(index * 180)));
        }

        for (var index = 0; index < 10; index++)
        {
            _recorder.OnClick(new ClickCounted(MouseButton.Left, TimeSpan.FromSeconds(5 + index)));
        }

        Assert.Equal(6, _recorder.Snapshot(0).BestClicksPerSecond);
    }

    [Fact]
    public void Media_time_goes_to_sources_and_favorites()
    {
        _recorder.OnMedia(new MediaTimeCounted("video", "youtube.com", 600));
        _recorder.OnMedia(new MediaTimeCounted("video", "twitch.tv", 60));
        _recorder.OnMedia(new MediaTimeCounted("music", "Spotify", 1200));
        _recorder.OnMedia(new MediaTimeCounted("game", "Portal 2", 3600));
        _recorder.OnMedia(new MediaTimeCounted("video", "youtube.com", 30));
        _recorder.OnMedia(new MediaTimeCounted("video", "bad", double.NaN));
        _recorder.AddProgram("Блокнот", 5);
        _recorder.AddProgram("Блокнот", 5);

        var snapshot = _recorder.Snapshot(0);

        Assert.Equal(690, snapshot.MediaSeconds["video"], 6);
        Assert.Equal(1200, snapshot.MediaSeconds["music"], 6);
        Assert.Equal(3600, snapshot.MediaSeconds["game"], 6);
        Assert.Equal(["Spotify", "youtube.com", "twitch.tv"], snapshot.TopSites.Select(entry => entry.Name));
        Assert.Equal(630, snapshot.TopSites[1].Seconds, 6);
        Assert.Equal("Portal 2", Assert.Single(snapshot.TopGames).Name);
        Assert.Equal(new UsageEntry("Блокнот", 10), Assert.Single(snapshot.TopPrograms));
        Assert.Equal(5490, Assert.Single(snapshot.Days).MediaSeconds, 6);
    }

    [Fact]
    public void Days_change_at_local_midnight_and_the_record_day_stays()
    {
        _recorder.OnEarned(new CoinsEarned("click", 50, 50));
        var first = LocalDate(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromDays(1));
        _recorder.OnEarned(new CoinsEarned("click", 20, 70));
        _clock.Advance(TimeSpan.FromDays(2));
        _recorder.OnEarned(new CoinsEarned("click", 30, 100));

        var snapshot = _recorder.Snapshot(100);

        Assert.Equal([50L, 20L, 30L], snapshot.Days.Select(day => day.Earned));
        Assert.Equal(first, snapshot.BestDay!.Date);
        Assert.Equal(50, snapshot.BestDay.Earned);

        var series = StatsSeries.Days(snapshot.Days, LocalDate(_clock.GetUtcNow()), 5, day => day.Earned);
        Assert.Equal([0L, 50L, 20L, 0L, 30L], series.Select(point => point.Value));
    }

    [Fact]
    public void Weeks_start_on_monday()
    {
        var monday = new DateOnly(2026, 11, 2);
        Assert.Equal(monday, StatsSeries.WeekStart(new DateOnly(2026, 11, 8)));
        Assert.Equal(monday, StatsSeries.WeekStart(monday));

        DayStats[] days =
        [
            new(new DateOnly(2026, 10, 26), 5, 0, 0, 0),
            new(new DateOnly(2026, 11, 1), 7, 0, 0, 0),
            new(new DateOnly(2026, 11, 3), 11, 0, 0, 0),
        ];
        var weeks = StatsSeries.Weeks(days, new DateOnly(2026, 11, 8), 3, day => day.Earned);
        Assert.Equal([new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 26), monday], weeks.Select(point => point.Start));
        Assert.Equal([0L, 12L, 11L], weeks.Select(point => point.Value));
    }

    [Fact]
    public void Statistics_survive_a_restart_through_the_save()
    {
        _recorder.OnEarned(new CoinsEarned("click", 4, 4));
        _recorder.OnClick(new ClickCounted(MouseButton.Left, TimeSpan.Zero));
        _recorder.OnUpgrade(new UpgradePurchased("clicks.left.plus", "Крепкий палец", 1, 60, _clock.GetUtcNow()), "куплено");
        _recorder.Post("star", "событие");
        _recorder.Save();

        var restarted = new StatsRecorder(_store, _clock).Snapshot(0);

        Assert.Equal(4, restarted.TotalEarned);
        Assert.Equal(1, restarted.Clicks["left"]);
        var upgrade = Assert.Single(restarted.Upgrades);
        Assert.Equal(("clicks.left.plus", 1, 60L, _clock.GetUtcNow()), (upgrade.Id, upgrade.Level, upgrade.Price, upgrade.When));
        Assert.Equal(["событие", "куплено"], restarted.Feed.Select(entry => entry.Text));
    }

    [Fact]
    public void Feed_and_usage_are_limited()
    {
        for (var index = 0; index < StatsRecorder.FeedLimit + 20; index++)
        {
            _recorder.Post("star", $"запись {index}");
        }

        for (var index = 0; index < StatsRecorder.UsageLimit + 50; index++)
        {
            _recorder.AddProgram($"программа {index}", 100 + index);
        }

        // Новое не вытесняется сразу, даже если у него меньше всего времени.
        _recorder.AddProgram("новая", 1);
        _recorder.AddProgram("новая", 1);

        var snapshot = _recorder.Snapshot(0);
        Assert.Equal(StatsRecorder.FeedLimit, snapshot.Feed.Count);
        Assert.Equal($"запись {StatsRecorder.FeedLimit + 19}", snapshot.Feed[0].Text);
        Assert.Equal($"программа {StatsRecorder.UsageLimit + 49}", snapshot.TopPrograms[0].Name);
        var saved = _store.Get<StatsState>(StatsRecorder.SaveKey);
        Assert.Null(saved);
        _recorder.Save();
        var programs = _store.Get<StatsState>(StatsRecorder.SaveKey)!.Programs;
        Assert.Equal(StatsRecorder.UsageLimit, programs.Count);
        Assert.Equal(2, programs["новая"]);
    }

    [Fact]
    public void Catalog_share_counts_unlocked_part()
    {
        var item = new CollectionItem("site:youtube.com", "YouTube", LockKind.Site, 800, _clock.GetUtcNow());
        _recorder.OnCollection(new CollectionChanged([item], [new CollectionCatalog(LockKind.Site, 4, 1), new CollectionCatalog(LockKind.Game, 0, 0)]));

        var snapshot = _recorder.Snapshot(0);

        Assert.Equal(0.25, snapshot.CatalogShare, 6);
        Assert.Equal(item, Assert.Single(snapshot.Collection));
    }

    [Fact]
    public void Export_writes_readable_json_only_to_the_chosen_file()
    {
        using var temp = new TempDirectory();
        _recorder.OnEarned(new CoinsEarned("click", 3, 3));
        _recorder.OnMedia(new MediaTimeCounted("video", "youtube.com", 60));
        var path = temp.Combine("stats.json");

        StatsExport.Write(_recorder.Snapshot(3), path);

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(3, json.RootElement.GetProperty("totalEarned").GetInt64());
        Assert.Equal("youtube.com", json.RootElement.GetProperty("topSites")[0].GetProperty("name").GetString());
        Assert.Equal(["stats.json"], Directory.GetFiles(temp.Path).Select(Path.GetFileName));
    }
}

public sealed class CollectionReportTests
{
    [Fact]
    public void Collection_has_dates_prices_and_catalog_size()
    {
        var log = new RecordingLog();
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), log);
        data.Load();
        var programs = ProgramCatalog.Load(data);
        var sites = SiteCatalog.Load(data);
        var first = programs.Programs[0];
        var when = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var unlocked = new Dictionary<string, UnlockRecord>
        {
            [$"program:{first.Id}"] = new(first.Name, first.Price, LockKind.Program, when),
            [SiteClassifier.KeyPrefix + sites.Sites[0].Domain] = new(sites.Sites[0].Name, sites.Sites[0].Price, LockKind.Site, when.AddDays(1)),
            ["file:unknown.exe"] = new("Неизвестная", 300, LockKind.Program, when.AddDays(-1)),
        };

        var report = CollectionReport.Build(unlocked, programs, sites, ["steam:1", "steam:2"]);

        Assert.Equal(sites.Sites[0].Name, report.Items[0].Name);
        Assert.Equal(when.AddDays(1), report.Items[0].When);
        Assert.Equal(first.Price, report.Items[1].Price);
        var program = report.Catalog.Single(part => part.Kind == LockKind.Program);
        Assert.Equal((programs.Programs.Count + 1, 2), (program.Total, program.Unlocked));
        Assert.Equal((2, 0), report.Catalog.Single(part => part.Kind == LockKind.Game) is var game ? (game.Total, game.Unlocked) : default);
        Assert.Equal((sites.Sites.Count, 1), report.Catalog.Single(part => part.Kind == LockKind.Site) is var site ? (site.Total, site.Unlocked) : default);
    }
}

public sealed class TimeStatsTests
{
    [Fact]
    public void Time_module_reports_real_seconds_by_site()
    {
        var clock = new ManualClock();
        var bus = new EventBus(new RecordingLog());
        var counted = new List<MediaTimeCounted>();
        bus.Subscribe<MediaTimeCounted>(counted.Add);
        var rewards = new TimeRewards(TimeRulesTests.LoadRules(), new TimeRewardsTests.RecordingEconomy(), bus, new RecordingLog(), clock);
        var browser = new ForegroundState(4242, @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        for (var second = 1; second <= 62; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            rewards.Tick(browser, TimeSpan.Zero);
            if (second % 2 == 0)
            {
                var element = new BrowserMediaElement("v", true, second, 3600, true, 1, true, "t");
                rewards.OnBrowserMedia(new BrowserMediaReported(1, 4242, [new BrowserTabMedia(1, "https://www.youtube.com/watch?v=x", true, [element])]));
            }
        }

        rewards.Flush();

        Assert.All(counted, item => Assert.Equal(("video", "youtube.com"), (item.Source, item.Subject)));
        Assert.InRange(counted.Sum(item => item.Seconds), 55, 62);
    }
}

/// <summary>Статистика загружается из папки modules, как в программе, и видит клики и покупки только через шину.</summary>
public sealed class StatsModuleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly string _modules;
    private EventBus _bus = null!;
    private CoinEconomy _economy = null!;
    private JsonFileStore _store = null!;
    private ModuleHost _host = null!;
    private StatsSnapshot? _snapshot;
    private double _time;

    public StatsModuleTests()
    {
        _modules = _temp.Combine("modules");
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Clicks", _modules);
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Stats", _modules);
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
        _bus.Subscribe<StatsChanged>(change => _snapshot = change.Snapshot);
        var texts = new TextCatalog(data, "ru", _log);
        texts.Use("ru");
        _host = new ModuleHost(_bus, data, texts, _economy, _store, _store, new RecoveryService(new ChangeJournal(_temp.Combine("recovery.json"), _log), () => [], _log), _log, developerMode: false);
        _host.Start(ModuleLoader.Load(_modules, _log));
    }

    private StatsSnapshot Request()
    {
        _snapshot = null;
        _bus.Publish(new StatsRequested());
        return _snapshot ?? throw new InvalidOperationException("Статистика не ответила");
    }

    private void Click(MouseButton button, bool injected = false)
    {
        _time += 300 + (_time % 97);
        _bus.Publish(new MouseButtonChanged(button, true, injected, TimeSpan.FromMilliseconds(_time)));
        _bus.Publish(new MouseButtonChanged(button, false, injected, TimeSpan.FromMilliseconds(_time + 40)));
    }

    [Fact]
    public void Clicks_coins_and_purchases_are_counted_and_survive_a_restart()
    {
        Assert.Equal(["clicks", "stats"], _host.Running.Select(module => module.Id).Order());
        for (var index = 0; index < 12; index++)
        {
            Click(MouseButton.Left);
        }

        Click(MouseButton.Right);
        Click(MouseButton.Left, injected: true);
        // Шина синхронна, пауза нужна только потоку статистики, который считает время в программе.
        Thread.Sleep(300);

        var when = DateTimeOffset.UtcNow;
        _economy.TrySpend(5, "unlock:site:youtube.com");
        _bus.Publish(new ItemUnlocked(new LockOffer("site:youtube.com", "YouTube", 5, LockKind.Site), when));
        _bus.Publish(new UpgradePurchased("clicks.left.plus", "Крепкий палец", 1, 0, when));

        var snapshot = Request();
        Assert.Equal(12, snapshot.Clicks["left"]);
        Assert.Equal(1, snapshot.Clicks["right"]);
        Assert.Equal(12, snapshot.TotalEarned);
        Assert.Equal(12, snapshot.EarnedBySource["click"]);
        Assert.Equal(5, snapshot.TotalSpent);
        Assert.Equal(7, snapshot.Balance);
        Assert.Equal(["Улучшение «Крепкий палец», ур. 1, за 0", "Куплено: YouTube за 5"], snapshot.Feed.Select(entry => entry.Text));
        Assert.True(snapshot.AppSeconds > 0);

        _host.StopAll();
        _store.Flush();
        Start();

        var restarted = Request();
        Assert.Equal(12, restarted.Clicks["left"]);
        Assert.Equal(12, restarted.TotalEarned);
        Assert.Single(restarted.Upgrades);
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Statistics_stay_on_this_computer()
    {
        // Модуль статистики не ссылается на сетевые сборки: данные физически некуда отправить.
        var path = Path.Combine(_modules, "ADHDWindows.Modules.Stats", "ADHDWindows.Modules.Stats.dll");
        var references = ReadReferences(path);
        Assert.Contains("ADHDWindows.Core", references);
        Assert.DoesNotContain(references, name => name.StartsWith("System.Net", StringComparison.Ordinal));
    }

    private static List<string> ReadReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new System.Reflection.PortableExecutable.PEReader(stream);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(reader);
        return [.. metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))];
    }
}

public sealed class ProgressResetTests : IDisposable
{
    private const string SampleRunsKey = "modules/sample/runs";

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly AppPaths _paths;

    public ProgressResetTests() => _paths = new AppPaths(_temp.Combine("install"), _temp.Combine("user"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Reset_clears_progress_keeps_settings_and_a_backup()
    {
        RepositoryRoot.DeploySampleModule(_paths.ModulesDir);
        using var host = new GameHost(_paths, new AppOptions(DeveloperMode: false), _log);
        host.Start();
        host.Economy.Earn("developer", 500);
        host.Settings.Set("language", "ru");
        var resets = 0;
        host.Events.Subscribe<ProgressReset>(_ => resets++);
        Assert.Equal(1, host.Saves.Get<int>(SampleRunsKey));

        var backup = host.ResetProgress();

        Assert.Equal(0, host.Economy.Balance);
        Assert.Equal("ru", host.Settings.Get<string>("language"));
        Assert.Equal(1, resets);

        // Модули запустились заново и видят пустое сохранение: это снова первый запуск.
        Assert.Equal("sample", Assert.Single(host.RunningModules).Id);
        Assert.Equal(1, host.Saves.Get<int>(SampleRunsKey));
        Assert.NotNull(backup);
        Assert.Contains("\"economy/balance\": 500", File.ReadAllText(backup));
        Assert.DoesNotContain("\"economy/balance\": 500", File.ReadAllText(_paths.SavePath));
        Assert.Empty(_log.Errors);
    }
}
