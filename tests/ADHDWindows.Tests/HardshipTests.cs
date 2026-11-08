using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Hardships;
using ADHDWindows.Modules.Hardships.Basic;

namespace ADHDWindows.Tests;

public sealed class HardshipSchedulerTests : IDisposable
{
    private static readonly HardshipInfo Easy = new("easyOne", HardshipLevel.Easy, HardshipResources.CursorMotion);
    private static readonly HardshipInfo Medium = new("mediumOne", HardshipLevel.Medium, HardshipResources.CursorLook);
    private static readonly HardshipInfo Hard = new("hardOne", HardshipLevel.Hard, HardshipResources.Windows);

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly ManualClock _clock = new();
    private readonly EventBus _bus;
    private readonly JsonFileStore _store;
    private readonly CoinEconomy _economy;
    private readonly TextCatalog _texts;
    private readonly List<object> _events = [];
    private readonly HashSet<string> _failing = [];
    private readonly HashSet<string> _running = [];

    public HardshipSchedulerTests()
    {
        _bus = new EventBus(_log);
        _store = new JsonFileStore(_temp.Combine("save.json"), _log);
        _store.Load();
        _economy = new CoinEconomy(_store, _bus, _clock);
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        _texts = new TextCatalog(data, "ru", _log);
        _texts.Use("ru");
        _bus.Subscribe<HardshipWarned>(_events.Add);
        _bus.Subscribe<HardshipStarted>(_events.Add);
        _bus.Subscribe<HardshipEnded>(_events.Add);
        _bus.Subscribe<HudStatusChanged>(_events.Add);
        _bus.Subscribe<HardshipStartRequested>(request =>
        {
            if (_failing.Contains(request.Id))
            {
                _bus.Publish(new HardshipFailed(request.Id, "test"));
            }
            else
            {
                _running.Add(request.Id);
            }
        });
        _bus.Subscribe<HardshipStopRequested>(request => _running.Remove(request.Id));
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(HardshipMode.Off, 5)]
    [InlineData(HardshipMode.Soft, 7.5)]
    [InlineData(HardshipMode.Normal, 10)]
    [InlineData(HardshipMode.Hardcore, 15)]
    public void Mode_multiplies_all_income(HardshipMode mode, double expected)
    {
        using var scheduler = Create();
        scheduler.SetMode(mode);

        Assert.Equal((decimal)expected, _economy.Preview("clicks", 10));
        Assert.Equal((decimal)expected, _economy.Preview("time", 10));
    }

    [Fact]
    public void Mode_survives_a_restart_and_the_multiplier_is_removed_on_dispose()
    {
        using (var scheduler = Create())
        {
            scheduler.SetMode(HardshipMode.Off);
        }

        Assert.Equal(10, _economy.Preview("clicks", 10));
        using var again = Create();
        Assert.Equal(HardshipMode.Off, again.Mode);
        Assert.Equal(5, _economy.Preview("clicks", 10));
    }

    [Fact]
    public void Warning_comes_first_then_the_hardship_runs_and_pays_a_bonus_once()
    {
        using var scheduler = Create(Easy);
        GoTo(scheduler, scheduler.Next);

        var warned = Assert.Single(_events.OfType<HardshipWarned>());
        Assert.Equal(TimeSpan.FromSeconds(10), warned.Delay);
        Assert.Empty(_running);
        Assert.Contains(_events.OfType<HudStatusChanged>(), status => status.Text == "hardships.easyOne.title через 0:10");

        Step(scheduler, TimeSpan.FromSeconds(10));
        Assert.Equal(["easyOne"], _running);
        var started = Assert.Single(_events.OfType<HardshipStarted>());
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(5), started.Ends);

        var before = _economy.Balance;
        Step(scheduler, TimeSpan.FromMinutes(5));
        Step(scheduler, TimeSpan.FromSeconds(1));

        Assert.Empty(_running);
        var ended = Assert.Single(_events.OfType<HardshipEnded>());
        Assert.Equal(HardshipEndReason.Survived, ended.Reason);
        Assert.Equal(15, ended.Bonus);
        Assert.Equal(before + 15, _economy.Balance);
        Assert.Null(_events.OfType<HudStatusChanged>().Last().Text);
    }

    [Fact]
    public void Harder_hardships_are_shorter()
    {
        foreach (var (info, min, max) in new[] { (Medium, 120, 180), (Hard, 45, 60) })
        {
            _events.Clear();
            using var scheduler = Create(info);
            GoTo(scheduler, scheduler.Next);
            Step(scheduler, TimeSpan.FromSeconds(10));
            var started = Assert.Single(_events.OfType<HardshipStarted>());
            var length = (started.Ends - _clock.GetUtcNow()).TotalSeconds;
            Assert.InRange(length, min, max);
            scheduler.Cancel();
        }
    }

    [Fact]
    public void Interval_has_a_spread_of_five_minutes()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            using var scheduler = Create([Easy], seed);
            var delay = scheduler.Next - _clock.GetUtcNow();
            Assert.InRange(delay.TotalMinutes, 15, 25);
            scheduler.Cancel();
            _store.Remove("saves/" + HardshipScheduler.NextSave);
        }
    }

    [Fact]
    public void Blocked_moment_postpones_the_hardship_without_warning()
    {
        using var scheduler = Create(Easy);
        var due = scheduler.Next;
        GoTo(scheduler, due, blocked: "fullscreen");

        Assert.Empty(_events.OfType<HardshipWarned>());
        Assert.Empty(_running);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(5), scheduler.Next);
    }

    [Fact]
    public void Block_during_warning_cancels_it()
    {
        using var scheduler = Create(Easy);
        GoTo(scheduler, scheduler.Next);
        Assert.NotNull(scheduler.Warned);

        scheduler.Tick("secure");
        Step(scheduler, TimeSpan.FromSeconds(15), "secure");

        Assert.Null(scheduler.Warned);
        Assert.Empty(_running);
        Assert.Empty(_events.OfType<HardshipStarted>());
    }

    [Theory]
    [InlineData("fullscreen")]
    [InlineData("secure")]
    [InlineData("password")]
    public void Block_while_active_removes_the_hardship_without_bonus(string reason)
    {
        using var scheduler = Create(Easy);
        GoTo(scheduler, scheduler.Next);
        Step(scheduler, TimeSpan.FromSeconds(10));
        Assert.NotNull(scheduler.Active);

        scheduler.Tick(reason);

        Assert.Empty(_running);
        Assert.Null(scheduler.Active);
        var ended = Assert.Single(_events.OfType<HardshipEnded>());
        Assert.Equal(HardshipEndReason.Interrupted, ended.Reason);
        Assert.Equal(0, ended.Bonus);
    }

    [Fact]
    public void Quiet_mode_postpones_and_expires()
    {
        using var scheduler = Create(Easy);
        var quietEnd = _clock.GetUtcNow() + TimeSpan.FromHours(1);
        scheduler.SetQuiet(TimeSpan.FromHours(1));
        GoTo(scheduler, scheduler.Next);
        Assert.Empty(_events.OfType<HardshipWarned>());
        Assert.Equal("Не беспокоить", _events.OfType<HudStatusChanged>().Last().Text);

        GoTo(scheduler, quietEnd - TimeSpan.FromSeconds(1));
        Assert.Empty(_events.OfType<HardshipWarned>());

        // Отложенное усложнение приходит не позже, чем через время откладывания после конца режима.
        GoTo(scheduler, quietEnd + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.NotEmpty(_events.OfType<HardshipWarned>());
    }

    [Fact]
    public void Quiet_mode_stops_the_active_hardship()
    {
        using var scheduler = Create(Easy);
        GoTo(scheduler, scheduler.Next);
        Step(scheduler, TimeSpan.FromSeconds(10));

        scheduler.SetQuiet(TimeSpan.FromMinutes(30));

        Assert.Empty(_running);
        Assert.Equal(HardshipEndReason.Interrupted, Assert.Single(_events.OfType<HardshipEnded>()).Reason);
    }

    [Fact]
    public void Off_mode_never_starts_and_soft_mode_starts_only_easy_ones()
    {
        using var scheduler = Create(Easy, Medium, Hard);
        scheduler.SetMode(HardshipMode.Off);
        GoTo(scheduler, _clock.GetUtcNow() + TimeSpan.FromHours(3));
        Assert.Empty(_events.OfType<HardshipWarned>());

        scheduler.SetMode(HardshipMode.Soft);
        for (var i = 0; i < 6; i++)
        {
            GoTo(scheduler, scheduler.Next);
            Step(scheduler, TimeSpan.FromSeconds(10));
            scheduler.Cancel();
        }

        Assert.All(_events.OfType<HardshipWarned>(), warned => Assert.Equal(HardshipLevel.Easy, warned.Hardship.Level));
        Assert.Equal(6, _events.OfType<HardshipStarted>().Count());
    }

    [Fact]
    public void Switching_to_soft_removes_a_running_hard_hardship()
    {
        using var scheduler = Create(Hard);
        GoTo(scheduler, scheduler.Next);
        Step(scheduler, TimeSpan.FromSeconds(10));
        Assert.Equal("hardOne", scheduler.Active?.Id);

        scheduler.SetMode(HardshipMode.Soft);

        Assert.Empty(_running);
        Assert.Equal(HardshipEndReason.Cancelled, Assert.Single(_events.OfType<HardshipEnded>()).Reason);
    }

    [Fact]
    public void Hardcore_is_more_frequent_and_harder()
    {
        var normal = Picks(HardshipMode.Normal, out var normalDelay);
        var hardcore = Picks(HardshipMode.Hardcore, out var hardcoreDelay);

        Assert.True(hardcoreDelay < normalDelay);
        Assert.True(hardcore[HardshipLevel.Hard] > normal[HardshipLevel.Hard]);
        Assert.True(hardcore[HardshipLevel.Easy] < normal[HardshipLevel.Easy]);
    }

    [Fact]
    public void Disabled_hardship_is_never_picked_and_disabling_stops_it()
    {
        using var scheduler = Create(Easy, Medium);
        scheduler.SetEnabled("mediumOne", false);
        for (var i = 0; i < 5; i++)
        {
            GoTo(scheduler, scheduler.Next);
            Step(scheduler, TimeSpan.FromSeconds(10));
            Assert.Equal("easyOne", scheduler.Active?.Id);
            scheduler.Cancel();
        }

        scheduler.SetEnabled("mediumOne", true);
        scheduler.SetEnabled("easyOne", false);
        GoTo(scheduler, scheduler.Next);
        Step(scheduler, TimeSpan.FromSeconds(10));
        Assert.Equal("mediumOne", scheduler.Active?.Id);

        scheduler.SetEnabled("mediumOne", false);

        Assert.Empty(_running);
        Assert.Null(scheduler.Active);
    }

    [Fact]
    public void Failed_start_is_reported_and_retried_later()
    {
        _failing.Add("easyOne");
        using var scheduler = Create(Easy);
        GoTo(scheduler, scheduler.Next);
        Step(scheduler, TimeSpan.FromSeconds(10));

        Assert.Null(scheduler.Active);
        Assert.Equal(HardshipEndReason.Failed, Assert.Single(_events.OfType<HardshipEnded>()).Reason);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(1), scheduler.Next);
    }

    [Fact]
    public void Only_one_hardship_runs_at_a_time()
    {
        using var scheduler = Create(Easy, Medium);
        GoTo(scheduler, scheduler.Next);
        Step(scheduler, TimeSpan.FromSeconds(10));
        Assert.True(scheduler.Test("mediumOne", null));

        Assert.Equal(["mediumOne"], _running);
        Assert.Equal(HardshipEndReason.Cancelled, Assert.Single(_events.OfType<HardshipEnded>()).Reason);
    }

    [Fact]
    public void Test_start_respects_safety()
    {
        using var scheduler = Create(Easy);

        Assert.False(scheduler.Test("easyOne", "secure"));
        Assert.Empty(_running);
        Assert.True(scheduler.Test("easyOne", null));
        Assert.Equal(["easyOne"], _running);
    }

    [Fact]
    public void Dispose_stops_the_active_hardship()
    {
        var scheduler = Create(Easy);
        scheduler.Test(null, null);
        Assert.NotEmpty(_running);

        scheduler.Dispose();

        Assert.Empty(_running);
        Assert.Equal(10, _economy.Preview("clicks", 10));
    }

    [Fact]
    public void Planned_time_survives_a_restart()
    {
        DateTimeOffset next;
        using (var scheduler = Create(Easy))
        {
            next = scheduler.Next;
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        using var again = Create(Easy);

        Assert.Equal(next, again.Next);
    }

    [Fact]
    public void Status_lists_hardships_and_settings()
    {
        HardshipStatus? status = null;
        _bus.Subscribe<HardshipStatus>(value => status = value);
        using var scheduler = Create(Hard, Easy);
        scheduler.SetEnabled("hardOne", false);

        Assert.NotNull(status);
        Assert.Equal(["easyOne", "hardOne"], status.Hardships.Select(entry => entry.Hardship.Id));
        Assert.Equal([true, false], status.Hardships.Select(entry => entry.Enabled));
        Assert.Equal(HardshipMode.Normal, status.Mode);
        Assert.Equal(1m, status.Multiplier);
    }

    [Fact]
    public void Rules_come_from_the_data_folder()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();

        var rules = HardshipRules.Load(data, _log);

        Assert.Empty(_log.Errors);
        Assert.Empty(_log.Warnings);
        Assert.Equal(20, rules.IntervalMinutes);
        Assert.Equal(new LevelRules(120, 180, 25), rules.For(HardshipLevel.Medium));
        Assert.Equal(0.5m, rules.For(HardshipMode.Off).Multiplier);
        Assert.Equal([1d, 2d, 3d], rules.For(HardshipMode.Hardcore).Weights);
    }

    private Dictionary<HardshipLevel, int> Picks(HardshipMode mode, out TimeSpan averageDelay)
    {
        var counts = Enum.GetValues<HardshipLevel>().ToDictionary(level => level, _ => 0);
        _store.Remove("saves/" + HardshipScheduler.NextSave);
        using var scheduler = Create([Easy, Medium, Hard], seed: 7);
        scheduler.SetMode(mode);
        var total = TimeSpan.Zero;
        const int rounds = 300;
        for (var i = 0; i < rounds; i++)
        {
            var from = _clock.GetUtcNow();
            GoTo(scheduler, scheduler.Next);
            total += _clock.GetUtcNow() - from;
            counts[scheduler.Warned!.Level]++;
            scheduler.Cancel();
        }

        averageDelay = total / rounds;
        return counts;
    }

    private HardshipScheduler Create(params HardshipInfo[] hardships) => Create(hardships, 1);

    private HardshipScheduler Create(HardshipInfo[] hardships, int seed)
    {
        var scheduler = new HardshipScheduler(
            HardshipRules.Default,
            _economy,
            _bus,
            new ScopedStore(_store, "saves"),
            new ScopedStore(_store, "settings"),
            _texts,
            _clock,
            new Random(seed),
            _log);
        _bus.Subscribe<HardshipFailed>(scheduler.OnFailed);
        foreach (var hardship in hardships)
        {
            scheduler.Offer(hardship);
        }

        return scheduler;
    }

    /// <summary>Перематывает время до <paramref name="moment"/> секундными шагами, как поток модуля.</summary>
    private void GoTo(HardshipScheduler scheduler, DateTimeOffset moment, string? blocked = null)
    {
        var limit = 0;
        while (_clock.GetUtcNow() < moment)
        {
            var step = moment - _clock.GetUtcNow();
            _clock.Advance(step > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : step);
            scheduler.Tick(blocked);
            Assert.True(++limit < 100_000);
        }

        scheduler.Tick(blocked);
    }

    private void Step(HardshipScheduler scheduler, TimeSpan time, string? blocked = null)
    {
        _clock.Advance(time);
        scheduler.Tick(blocked);
    }
}

public sealed class HardshipModuleTests
{
    private readonly RecordingLog _log = new();

    [Fact]
    public void Base_class_answers_requests_starts_once_and_stops_with_the_app()
    {
        var bus = new EventBus(_log);
        var offered = new List<HardshipInfo>();
        bus.Subscribe<HardshipOffered>(offer => offered.Add(offer.Hardship));
        var module = new ProbeHardship();
        using var temp = new TempDirectory();
        var store = new JsonFileStore(temp.Combine("s.json"), _log);
        var host = new ModuleHost(bus, new GameData(temp.Path, _log), new TextCatalog(new GameData(temp.Path, _log), "ru", _log), new CoinEconomy(store, bus, TimeProvider.System), store, store, new RecoveryService(new ChangeJournal(temp.Combine("r.json"), _log), () => [], _log), _log, developerMode: false);
        host.Start([module]);

        Assert.Equal(["probe"], offered.Select(info => info.Id));
        bus.Publish(new HardshipsRequested());
        Assert.Equal(2, offered.Count);
        Assert.Equal("hardship.probe", module.Id);

        bus.Publish(new HardshipStartRequested("other"));
        Assert.Equal(0, module.Begins);
        bus.Publish(new HardshipStartRequested("probe"));
        bus.Publish(new HardshipStartRequested("probe"));
        Assert.Equal(1, module.Begins);
        Assert.True(module.IsActive);

        host.StopAll();

        Assert.Equal(1, module.Ends);
        Assert.False(module.IsActive);
    }

    [Fact]
    public void Failed_begin_is_undone_and_reported()
    {
        var bus = new EventBus(_log);
        var failures = new List<HardshipFailed>();
        bus.Subscribe<HardshipFailed>(failures.Add);
        var module = new ProbeHardship { Fail = true };
        using var temp = new TempDirectory();
        var store = new JsonFileStore(temp.Combine("s.json"), _log);
        var host = new ModuleHost(bus, new GameData(temp.Path, _log), new TextCatalog(new GameData(temp.Path, _log), "ru", _log), new CoinEconomy(store, bus, TimeProvider.System), store, store, new RecoveryService(new ChangeJournal(temp.Combine("r.json"), _log), () => [], _log), _log, developerMode: false);
        host.Start([module]);

        bus.Publish(new HardshipStartRequested("probe"));

        Assert.Equal("probe", Assert.Single(failures).Id);
        Assert.False(module.IsActive);
        Assert.Equal(1, module.Ends);
        host.StopAll();
        Assert.Equal(1, module.Ends);
    }

    [Fact]
    public void Scheduler_and_basic_hardships_load_from_the_modules_folder()
    {
        using var temp = new TempDirectory();
        var modules = temp.Combine("modules");
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Hardships", modules);
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Hardships.Basic", modules);
        var bus = new EventBus(_log);
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        var store = new JsonFileStore(temp.Combine("save.json"), _log);
        store.Load();
        var economy = new CoinEconomy(store, bus, TimeProvider.System);
        var texts = new TextCatalog(data, "ru", _log);
        texts.Use("ru");
        HardshipStatus? status = null;
        bus.Subscribe<HardshipStatus>(value => status = value);
        var host = new ModuleHost(bus, data, texts, economy, store, store, new RecoveryService(new ChangeJournal(temp.Combine("recovery.json"), _log), () => [], _log), _log, developerMode: false);
        host.Start(ModuleLoader.Load(modules, _log));
        try
        {
            Assert.Contains("hardships", host.Running.Select(module => module.Id));
            Assert.NotNull(status);
            Assert.Equal(
                ["gravity", "invisible", "oneWindow", "pollution"],
                status.Hardships.Select(entry => entry.Hardship.Id).Order(StringComparer.Ordinal));
            Assert.All(status.Hardships, entry => Assert.DoesNotContain("hardships.", texts.Get($"hardships.{entry.Hardship.Id}.title")));

            var restorers = ModuleLoader.LoadRestorers(modules, _log).Select(restorer => restorer.Kind).ToList();
            Assert.Contains(CursorRestorer.ChangeKind, restorers);
            Assert.Contains(MouseSpeedRestorer.ChangeKind, restorers);
            Assert.Contains(MinimizedWindowsRestorer.ChangeKind, restorers);
            Assert.Empty(_log.Errors);
        }
        finally
        {
            host.StopAll();
        }
    }

    private sealed class ProbeHardship : HardshipModule
    {
        public bool Fail { get; init; }

        public int Begins { get; private set; }

        public int Ends { get; private set; }

        public override HardshipInfo Info { get; } = new("probe", HardshipLevel.Easy, HardshipResources.None);

        protected override void Begin()
        {
            Begins++;
            if (Fail)
            {
                throw new InvalidOperationException("test");
            }
        }

        protected override void End() => Ends++;
    }
}

public sealed class QuietDetectorTests
{
    [Fact]
    public void Normal_desktop_is_not_a_secure_screen_and_the_check_does_not_throw()
    {
        Assert.False(ADHDWindows.Platform.QuietDetector.IsSecureDesktop());

        // Остальные причины зависят от того, что сейчас делает человек, поэтому проверяется только, что ответ корректен.
        var reason = ADHDWindows.Platform.QuietDetector.Check();
        Assert.True(reason is null or "password" or "fullscreen" or "presentation" or "call", reason);
    }
}

public sealed class DirtTests
{
    [Fact]
    public void Movement_and_transitions_make_the_cursor_dirty_and_slow()
    {
        var dirt = new DirtMeter();
        Assert.Equal(DirtMeter.Initial, dirt.Level);
        var clean = dirt.SpeedFor(10);

        for (var i = 0; i < 400; i++)
        {
            dirt.Move(0, 30, 0.016);
        }

        dirt.Transition();
        Assert.True(dirt.Level > 0.5);
        Assert.True(dirt.SpeedFor(10) < clean);
        Assert.InRange(dirt.SpeedFor(10), 1, 10);
        Assert.Equal(0, dirt.Shakes);
    }

    [Fact]
    public void Quick_shaking_cleans_and_slow_wandering_does_not()
    {
        var dirt = new DirtMeter();
        for (var i = 0; i < 10; i++)
        {
            dirt.Transition();
        }

        Assert.True(dirt.Level > 0.999);

        // Медленные широкие движения туда-сюда: смена направления раз в две секунды.
        for (var turn = 0; turn < 6; turn++)
        {
            var direction = turn % 2 == 0 ? 1 : -1;
            for (var i = 0; i < 100; i++)
            {
                dirt.Move(direction * 2, 0, 0.02);
            }
        }

        Assert.Equal(0, dirt.Shakes);

        // Встряска: пять рывков по 100 пикселей за полсекунды.
        for (var turn = 0; turn < 5; turn++)
        {
            var direction = turn % 2 == 0 ? 1 : -1;
            for (var i = 0; i < 5; i++)
            {
                dirt.Move(direction * 20, 0, 0.016);
            }
        }

        Assert.Equal(1, dirt.Shakes);
        Assert.True(dirt.Level < 0.7);
    }

    [Fact]
    public void Cursor_art_is_blank_or_a_dirty_arrow()
    {
        Assert.Equal(0, CursorArt.Visible(CursorArt.Blank(32)));

        var clean = CursorArt.DirtyArrow(32, 0);
        var dirty = CursorArt.DirtyArrow(32, 1);
        Assert.InRange(CursorArt.Visible(clean), 80, 200);
        Assert.True(CursorArt.Visible(dirty) >= CursorArt.Visible(clean));
        Assert.True(Brown(dirty) > Brown(CursorArt.DirtyArrow(32, 0.4)));
        Assert.True(Brown(CursorArt.DirtyArrow(32, 0.4)) > 0);
        Assert.Equal(0, Brown(clean));

        // На крупном курсоре стрелка увеличена целиком, без сглаживания.
        Assert.Equal(CursorArt.Visible(clean) * 4, CursorArt.Visible(CursorArt.DirtyArrow(64, 0)));
    }

    private static int Brown(uint[] pixels) => pixels.Count(pixel => pixel is 0xFF5A3A22 or 0xFF8A5A2E or 0xFF3E2716);
}
