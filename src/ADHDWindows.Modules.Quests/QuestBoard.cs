using System.Globalization;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.Stats;
using ADHDWindows.Core.Storage;
using ADHDWindows.Core.Upgrades;

namespace ADHDWindows.Modules.Quests;

/// <summary>Настройки заданий. <paramref name="Warmup"/> — пауза после запуска, пока другие модули сообщают игры и покупки.</summary>
public sealed record QuestOptions(
    IReadOnlyDictionary<QuestTier, TimeSpan> Intervals,
    bool Notify,
    IReadOnlySet<string> DisabledTypes,
    TimeSpan Warmup)
{
    public static readonly IReadOnlyDictionary<QuestTier, TimeSpan> DefaultIntervals = new Dictionary<QuestTier, TimeSpan>
    {
        [QuestTier.Easy] = TimeSpan.FromMinutes(15),
        [QuestTier.Medium] = TimeSpan.FromMinutes(30),
        [QuestTier.Hard] = TimeSpan.FromMinutes(60),
    };

    public static QuestOptions Default { get; } = new(DefaultIntervals, true, new HashSet<string>(), TimeSpan.Zero);
}

/// <summary>Выданное задание в сохранении. Игра запоминается, чтобы прогресс засчитывался только за неё.</summary>
public sealed record QuestState(
    string Id,
    string Template,
    QuestTier Tier,
    string Title,
    string Icon,
    double Goal,
    QuestUnit Unit,
    long Reward,
    DateTimeOffset Issued,
    DateTimeOffset Refresh,
    double Progress,
    bool Completed,
    string? GameKey = null,
    string? GameName = null,
    string? Url = null,
    double[][]? Heard = null)
{
    public QuestView View() => new(Id, Template, Tier, Title, Icon, Progress, Goal, Unit, Reward, Issued, Refresh, Completed, Url);
}

/// <summary>
/// Доска заданий: одно задание на каждую сложность. Таймер сложности меняет задание на новое, выполнено оно или нет.
/// Задание выдаётся, только если его можно выполнить. Награда приходит ровно один раз.
/// Методы можно вызывать из любого потока, события рассылаются вне замка.
/// </summary>
public sealed class QuestBoard : IDisposable
{
    public const string ActiveKey = "active";

    /// <summary>Ключ сохранения: сколько заданий выполнено подряд в текущей цепочке.</summary>
    public const string ChainKey = "chain";

    /// <summary>Длина цепочки: столько заданий подряд закрывают её (идея 008, сундук — идея 011).</summary>
    public const int ChainGoal = 3;

    /// <summary>Источник дохода наград за задания.</summary>
    public const string RewardSource = "quest";

    private const int RewardStep = 5;

    /// <summary>Если подходящих шаблонов нет, следующая попытка — через эту паузу, а не каждую секунду.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>Отчёты плеера дальше друг от друга не связываются в отрезок прослушивания.</summary>
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(15);

    /// <summary>Небольшой скачок позиции сверх ожидаемого: задержки отчётов и округление.</summary>
    private const double SeekTolerance = 1.5;

    /// <summary>Самая большая скорость плеера, на которой трек считается прослушанным.</summary>
    private const double MaxListenRate = 2;

    /// <summary>Элементы, чья длительность отличается от трека больше этого, — другие звуки страницы, например реклама.</summary>
    private const double TrackDurationTolerance = 2;

    /// <summary>Докуда нужно прокрутить страницу, чтобы она считалась прочитанной.</summary>
    private const double ReadShare = 0.9;

    private readonly Dictionary<string, QuestTemplate> _templates;
    private readonly QuestOptions _options;
    private readonly IEconomy _economy;
    private readonly IEventBus _events;
    private readonly IKeyValueStore _saves;
    private readonly TextCatalog _texts;
    private readonly TimeProvider _clock;
    private readonly Random _random;
    private readonly ILog _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<QuestTier, QuestState> _active = [];
    private readonly Dictionary<QuestTier, DateTimeOffset> _retryAt = [];
    private readonly DateTimeOffset _startedAt;
    private IReadOnlyList<GameInstall>? _games;
    private HashSet<string>? _unlockedGames;
    private Dictionary<LockKind, int>? _remaining;
    private bool _canUpgrade;
    private bool _changed;
    private string? _hudText;
    private bool _hudPublished;
    private readonly LinkStatus _links;
    private readonly Dictionary<(int Peer, int Tab, string Element), Sample> _samples = [];
    private readonly string[] _urls;
    private int _chain;
    private IReadOnlyList<QuestPage>? _pages;

    public QuestBoard(
        IReadOnlyList<QuestTemplate> templates,
        QuestOptions options,
        IEconomy economy,
        IEventBus events,
        IKeyValueStore saves,
        TextCatalog texts,
        TimeProvider clock,
        Random random,
        ILog log,
        ILinkChecker? links = null)
    {
        _templates = templates.ToDictionary(template => template.Id, StringComparer.Ordinal);
        _options = options;
        _economy = economy;
        _events = events;
        _saves = saves;
        _texts = texts;
        _clock = clock;
        _random = random;
        _log = log;
        _startedAt = clock.GetUtcNow();
        _links = new LinkStatus(links ?? OpenLinkChecker.Instance, clock, log);
        _links.Checked += OnLinkChecked;
        _urls = [.. templates
            .Where(template => template.Url is not null && !options.DisabledTypes.Contains(template.Type))
            .Select(template => template.Url!)
            .Distinct(StringComparer.Ordinal)];
        _chain = Math.Clamp(saves.Get<int>(ChainKey), 0, ChainGoal - 1);

        foreach (var state in saves.Get<List<QuestState>>(ActiveKey) ?? [])
        {
            // Шаблон мог пропасть из данных: такое задание не отследить, его заменит новое.
            if (_templates.TryGetValue(state.Template, out var template) && template.Tier == state.Tier)
            {
                _active[state.Tier] = state;
            }
        }
    }

    /// <summary>Текущие задания по порядку сложности.</summary>
    public IReadOnlyList<QuestView> Snapshot()
    {
        lock (_lock)
        {
            return SnapshotLocked();
        }
    }

    /// <summary>Сколько заданий выполнено подряд в текущей цепочке.</summary>
    public int Chain
    {
        get
        {
            lock (_lock)
            {
                return _chain;
            }
        }
    }

    public void Dispose()
    {
        _links.Checked -= OnLinkChecked;
        _links.Dispose();
    }

    /// <summary>Рассылает задания, страницы заданий и строку HUD.</summary>
    public void Publish()
    {
        IReadOnlyList<QuestView> quests;
        lock (_lock)
        {
            quests = SnapshotLocked();
            _changed = false;
        }

        PublishState(quests, force: true);
    }

    /// <summary>Раз в секунду: смена заданий по таймерам и рассылка накопленного прогресса.</summary>
    public void Tick()
    {
        // Ссылки проверяются в фоне заранее: задание со ссылкой выдаётся, только когда она проверена.
        _links.Ensure(_urls);
        var now = _clock.GetUtcNow();
        var issued = new List<QuestView>();
        IReadOnlyList<QuestView>? quests = null;
        lock (_lock)
        {
            if (now - _startedAt >= _options.Warmup)
            {
                foreach (var tier in Enum.GetValues<QuestTier>())
                {
                    var due = _active.TryGetValue(tier, out var current) ? now >= current.Refresh : now >= _retryAt.GetValueOrDefault(tier);
                    if (due && current is { Completed: false } && _chain > 0)
                    {
                        // Невыполненное задание сменилось по таймеру: цепочка прервана.
                        _log.Info($"Задание {current.Template} не выполнено вовремя: цепочка {_chain}/{ChainGoal} обнулена");
                        _chain = 0;
                    }

                    if (due && Issue(tier, now, now + Interval(tier)) is { } quest)
                    {
                        issued.Add(quest);
                    }
                }
            }

            if (_changed)
            {
                _changed = false;
                Save();
                quests = SnapshotLocked();
            }
        }

        Announce(issued);
        if (quests is not null)
        {
            PublishState(quests, force: false);
        }
    }

    /// <summary>Все задания сменятся при следующем такте.</summary>
    public void RefreshAll()
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            foreach (var tier in _active.Keys.ToList())
            {
                _active[tier] = _active[tier] with { Refresh = now };
            }

            _retryAt.Clear();
        }
    }

    public void OnClick(ClickCounted click)
    {
        var button = click.Button.ToString().ToLowerInvariant();
        Advance((_, template) => template.Type == QuestTypes.Clicks && (template.Button is null || template.Button == button), 1);
    }

    public void OnMedia(MediaTimeCounted counted)
    {
        if (!(counted.Seconds > 0) || !double.IsFinite(counted.Seconds))
        {
            return;
        }

        Advance(
            (state, template) => template.Type switch
            {
                QuestTypes.Media => template.Source is null || template.Source == counted.Source,
                QuestTypes.Game => counted.Source == "game" && string.Equals(state.GameName, counted.Subject, StringComparison.OrdinalIgnoreCase),
                _ => false,
            },
            counted.Seconds);
    }

    public void OnSpent(CoinsSpent spent)
    {
        if (spent.Amount > 0)
        {
            Advance((_, template) => template.Type == QuestTypes.Spend, spent.Amount);
        }
    }

    public void OnUpgrade(UpgradePurchased _) => Advance((_, template) => template.Type == QuestTypes.Upgrade, 1);

    public void OnUnlock(ItemUnlocked unlocked)
    {
        var kind = unlocked.Offer.Kind.ToString().ToLowerInvariant();
        Advance((_, template) => template.Type == QuestTypes.Unlock && (template.Kind is null || template.Kind == kind), 1);
    }

    /// <summary>Действие на странице задания: открытие, прокрутка, выделение.</summary>
    public void OnPage(BrowserPageReported report)
    {
        var page = string.IsNullOrEmpty(report.Page) ? PageAddress.Normalize(report.Url) : report.Page;
        if (page is null)
        {
            return;
        }

        Apply((state, template) => template.Page == page && Matches(template, report) ? state with { Progress = state.Goal } : null);
    }

    /// <summary>
    /// Отчёт плеера: задание на прослушивание засчитывает только естественное воспроизведение во вкладке с его страницей.
    /// Позиция между двумя отчётами должна вырасти примерно на реальное время × скорость; перемотка ничего не даёт,
    /// а прослушанные куски трека считаются один раз.
    /// </summary>
    public void OnBrowserMedia(BrowserMediaReported report)
    {
        var now = _clock.GetUtcNow();
        var segments = new Dictionary<string, List<(double From, double To)>>(StringComparer.Ordinal);
        var durations = new Dictionary<string, double>(StringComparer.Ordinal);
        lock (_lock)
        {
            var pages = new HashSet<string>(StringComparer.Ordinal);
            if (!report.Disconnected)
            {
                foreach (var state in _active.Values)
                {
                    if (!state.Completed && _templates.TryGetValue(state.Template, out var template) && template.Type == QuestTypes.Listen && template.Page is { } page)
                    {
                        pages.Add(page);
                    }
                }
            }

            var seen = new HashSet<(int, int, string)>();
            foreach (var tab in pages.Count == 0 ? [] : report.Tabs)
            {
                if (PageAddress.Normalize(tab.Url) is not { } page || !pages.Contains(page))
                {
                    continue;
                }

                var track = tab.Media.Where(media => media.Duration is > 0 and < double.PositiveInfinity).MaxBy(media => media.Duration);
                if (track?.Duration is not { } trackDuration)
                {
                    continue;
                }

                durations[page] = Math.Max(durations.GetValueOrDefault(page), trackDuration);
                foreach (var media in tab.Media.Where(media => media.Duration is { } duration && Math.Abs(duration - trackDuration) <= TrackDurationTolerance))
                {
                    var key = (report.PeerId, tab.TabId, media.Id);
                    seen.Add(key);
                    var previous = _samples.GetValueOrDefault(key);
                    _samples[key] = new Sample(media.Position, now, media.Playing && media.Audible, media.Rate);
                    if (Natural(previous, media, now) is { } segment)
                    {
                        if (!segments.TryGetValue(page, out var list))
                        {
                            segments[page] = list = [];
                        }

                        list.Add(segment);
                    }
                }
            }

            foreach (var key in _samples.Keys.Where(key => key.Peer == report.PeerId && !seen.Contains(key)).ToList())
            {
                _samples.Remove(key);
            }
        }

        if (durations.Count == 0)
        {
            return;
        }

        Apply((state, template) =>
        {
            if (template.Type != QuestTypes.Listen || template.Page is not { } page || !durations.TryGetValue(page, out var duration))
            {
                return null;
            }

            var heard = Merge(state.Heard, segments.GetValueOrDefault(page) ?? [], duration);
            var progress = heard.Sum(part => part[1] - part[0]);
            var goal = Math.Max(1, Math.Round(duration * QuestTemplate.ListenShare));
            return progress == state.Progress && goal == state.Goal
                ? null
                : state with { Heard = heard, Progress = Math.Min(progress, goal), Goal = goal };
        });
    }

    /// <summary>Установленные игры. Задание на удалённую игру сразу заменяется.</summary>
    public void OnGames(IReadOnlyList<GameInstall> games)
    {
        List<QuestView> issued;
        lock (_lock)
        {
            _games = games;
            issued = ReplaceUnplayable();
        }

        Announce(issued);
    }

    /// <summary>Купленное: игры для заданий и сколько ещё можно купить.</summary>
    public void OnCollection(CollectionChanged collection)
    {
        List<QuestView> issued;
        lock (_lock)
        {
            _unlockedGames = collection.Items.Where(item => item.Kind == LockKind.Game).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            _remaining = collection.Catalog.GroupBy(part => part.Kind).ToDictionary(group => group.Key, group => group.Sum(part => Math.Max(0, part.Total - part.Unlocked)));
            issued = ReplaceUnplayable();
        }

        Announce(issued);
    }

    /// <summary>Есть ли что купить в магазине улучшений прямо сейчас.</summary>
    public void OnShop(UpgradeShopChanged shop)
    {
        lock (_lock)
        {
            _canUpgrade = shop.Branches.SelectMany(branch => branch.Offers).Any(offer => offer is { IsMaxed: false, IsLocked: false });
        }
    }

    /// <summary>Игры, которые можно выдать в задание: установлены и куплены. Заблокированные в задания не попадают.</summary>
    internal IReadOnlyList<GameInstall> PlayableGames()
    {
        lock (_lock)
        {
            return PlayableLocked();
        }
    }

    private List<GameInstall> PlayableLocked() =>
        _games is null || _unlockedGames is null ? [] : [.. _games.Where(game => _unlockedGames.Contains(game.Key))];

    private TimeSpan Interval(QuestTier tier) =>
        _options.Intervals.TryGetValue(tier, out var interval) && interval > TimeSpan.Zero ? interval : QuestOptions.DefaultIntervals[tier];

    private List<QuestView> SnapshotLocked() =>
        [.. _active.Values.OrderBy(state => state.Tier).Select(state => state.View())];

    private void Save()
    {
        _saves.Set(ActiveKey, _active.Values.OrderBy(state => state.Tier).ToList());
        _saves.Set(ChainKey, _chain);
    }

    private List<QuestView> ReplaceUnplayable()
    {
        var issued = new List<QuestView>();
        var playable = PlayableLocked().Select(game => game.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var state in _active.Values.ToList())
        {
            if (state is { Completed: false, GameKey: { } key } && !playable.Contains(key))
            {
                _log.Info($"Игра задания {state.Template} больше недоступна: задание заменено");
                if (Issue(state.Tier, _clock.GetUtcNow(), state.Refresh) is { } quest)
                {
                    issued.Add(quest);
                }
            }
        }

        return issued;
    }

    /// <summary>Выдаёт новое задание сложности или убирает старое, если выполнимых шаблонов нет.</summary>
    private QuestView? Issue(QuestTier tier, DateTimeOffset now, DateTimeOffset refresh)
    {
        _changed = true;
        var previous = _active.GetValueOrDefault(tier)?.Template;
        var playable = PlayableLocked();
        var candidates = _templates.Values
            .Where(template => template.Tier == tier && !_options.DisabledTypes.Contains(template.Type) && Feasible(template, playable))
            .ToList();

        // Тот же шаблон дважды подряд не выпадает, если есть из чего выбрать.
        if (candidates.Count > 1)
        {
            candidates.RemoveAll(template => template.Id == previous);
        }

        if (candidates.Count == 0)
        {
            _active.Remove(tier);
            _retryAt[tier] = now + RetryDelay;
            _log.Warn($"Нет выполнимых заданий сложности {tier}");
            return null;
        }

        var chosen = Pick(candidates);
        var game = chosen.Type == QuestTypes.Game ? playable[_random.Next(playable.Count)] : null;
        var title = chosen.Title
            .Replace("{goal}", chosen.Goal.ToString("0.#", CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("{game}", game?.Name ?? string.Empty, StringComparison.Ordinal)
            .Replace("{word}", chosen.Word ?? string.Empty, StringComparison.Ordinal);
        var state = new QuestState(
            Guid.NewGuid().ToString("N"),
            chosen.Id,
            tier,
            title,
            chosen.Icon,
            chosen.GoalValue,
            chosen.Unit,
            RollReward(chosen),
            now,
            refresh,
            0,
            false,
            game?.Key,
            game?.Name,
            chosen.Url);
        _active[tier] = state;
        _log.Info($"Выдано задание {chosen.Id} ({tier}): «{title}», награда {state.Reward}");
        return state.View();
    }

    private bool Feasible(QuestTemplate template, IReadOnlyList<GameInstall> playable) => template.Type switch
    {
        _ when template.Url is not null => _links.IsOpen(template.Url),
        QuestTypes.Game => playable.Count > 0,
        QuestTypes.Media => template.Source != "game" || playable.Count > 0,
        QuestTypes.Upgrade => _canUpgrade,
        QuestTypes.Unlock => _remaining is { } remaining && (template.Kind is null
            ? remaining.Values.Any(count => count > 0)
            : remaining.GetValueOrDefault(Enum.Parse<LockKind>(template.Kind, ignoreCase: true)) > 0),
        _ => true,
    };

    private QuestTemplate Pick(List<QuestTemplate> candidates)
    {
        var roll = _random.Next(candidates.Sum(template => template.Weight));
        foreach (var template in candidates)
        {
            roll -= template.Weight;
            if (roll < 0)
            {
                return template;
            }
        }

        return candidates[^1];
    }

    /// <summary>Случайная награда из диапазона шаблона, кратная пяти, если диапазон это позволяет.</summary>
    private long RollReward(QuestTemplate template)
    {
        var value = template.RewardMin + _random.NextInt64(template.RewardMax - template.RewardMin + 1);
        var rounded = (long)Math.Round(value / (double)RewardStep) * RewardStep;
        return rounded >= template.RewardMin && rounded <= template.RewardMax ? rounded : value;
    }

    private void Advance(Func<QuestState, QuestTemplate, bool> counts, double amount) =>
        Apply((state, template) => counts(state, template) ? state with { Progress = state.Progress + amount } : null);

    /// <summary>
    /// Меняет невыполненные задания: <paramref name="change"/> возвращает новое состояние или null, если задание не затронуто.
    /// Задание с прогрессом не меньше цели выполняется, награда приходит один раз, цепочка растёт.
    /// </summary>
    private void Apply(Func<QuestState, QuestTemplate, QuestState?> change)
    {
        var completed = new List<QuestState>();
        var chains = 0;
        lock (_lock)
        {
            foreach (var state in _active.Values.ToList())
            {
                if (state.Completed || !_templates.TryGetValue(state.Template, out var template) || change(state, template) is not { } next)
                {
                    continue;
                }

                if (next.Progress >= next.Goal)
                {
                    // Отметка ставится под замком до начисления: второе событие уже не выдаст награду.
                    var done = next with { Progress = next.Goal, Completed = true };
                    _active[state.Tier] = done;
                    completed.Add(done);
                    if (++_chain >= ChainGoal)
                    {
                        chains++;
                        _chain = 0;
                    }
                }
                else
                {
                    _active[state.Tier] = next;
                }

                _changed = true;
            }

            if (completed.Count > 0)
            {
                _changed = false;
                Save();
            }
        }

        if (completed.Count == 0)
        {
            return;
        }

        foreach (var state in completed)
        {
            var earned = _economy.Earn(RewardSource, state.Reward);
            _log.Info($"Выполнено задание {state.Template} ({state.Tier}): +{earned}");
            _events.Publish(new StatCounted($"quests.completed.{state.Tier.ToString().ToLowerInvariant()}"));
            _events.Publish(new StatCounted("quests.coins", earned));
            _events.Publish(new FeedPosted("quest", _texts.Get("feed.quest", state.Title, CoinFormat.Short(earned))));
            _events.Publish(new QuestCompleted(state.View(), earned, _options.Notify));
        }

        for (var chain = 0; chain < chains; chain++)
        {
            _log.Info($"Цепочка из {ChainGoal} заданий подряд закрыта");
            _events.Publish(new StatCounted("quests.chains"));
            _events.Publish(new FeedPosted("chest", _texts.Get("feed.quest.chain", ChainGoal)));
            _events.Publish(new QuestChainCompleted(ChainGoal));
        }

        _events.Publish(new SaveRequested());
        PublishState(Snapshot(), force: false);
    }

    private static bool Matches(QuestTemplate template, BrowserPageReported report) => template.Type switch
    {
        // Любое действие на странице значит, что она открыта.
        QuestTypes.Visit => true,
        QuestTypes.Read => report is { Kind: PageActivityKind.Scroll, Focused: true, Share: >= ReadShare },
        QuestTypes.Select => report.Kind == PageActivityKind.Selection
            && QuestCatalog.NormalizeWord(report.Text) is { Length: > 0 } text
            && text == QuestCatalog.NormalizeWord(template.Word),
        _ => false,
    };

    /// <summary>Отрезок трека между двумя отчётами, если он прозвучал естественно; иначе null.</summary>
    private static (double From, double To)? Natural(Sample? previous, BrowserMediaElement media, DateTimeOffset now)
    {
        if (previous is not { Playing: true, Position: { } from } || !media.Playing || !media.Audible || media.Position is not { } to)
        {
            return null;
        }

        var elapsed = now - previous.Time;
        if (elapsed <= TimeSpan.Zero || elapsed > MaxSampleGap || !(to > from))
        {
            return null;
        }

        // Быстрее ×2 трек уже не слушают, а проматывают: такой отрезок не засчитывается.
        var rate = Math.Max(previous.Rate, media.Rate);
        if (rate > MaxListenRate)
        {
            return null;
        }

        rate = Math.Max(rate, 0.0625);
        var expected = elapsed.TotalSeconds * rate;
        return to - from <= expected + Math.Max(SeekTolerance, expected * 0.25) ? (from, to) : null;
    }

    /// <summary>Прослушанные куски трека без пересечений, в пределах длительности.</summary>
    private static double[][] Merge(double[][]? heard, List<(double From, double To)> added, double duration)
    {
        var parts = (heard ?? [])
            .Where(part => part is [_, _])
            .Select(part => (From: part[0], To: part[1]))
            .Concat(added)
            .Select(part => (From: Math.Clamp(part.From, 0, duration), To: Math.Clamp(part.To, 0, duration)))
            .Where(part => part.To > part.From)
            .OrderBy(part => part.From)
            .ToList();
        var merged = new List<double[]>();
        foreach (var (from, to) in parts)
        {
            if (merged.Count > 0 && from <= merged[^1][1])
            {
                merged[^1][1] = Math.Max(merged[^1][1], to);
            }
            else
            {
                merged.Add([from, to]);
            }
        }

        return [.. merged];
    }

    private void OnLinkChecked(string url, bool open)
    {
        if (!open)
        {
            return;
        }

        // Пустая сложность могла ждать именно эту ссылку: следующий такт попробует выдать задание сразу.
        lock (_lock)
        {
            foreach (var tier in Enum.GetValues<QuestTier>().Where(tier => !_active.ContainsKey(tier)))
            {
                _retryAt.Remove(tier);
            }
        }
    }

    /// <summary>Рассылает страницы текущих заданий, например в ответ на запрос блокировки.</summary>
    public void PublishPages()
    {
        IReadOnlyList<QuestView> quests;
        lock (_lock)
        {
            quests = SnapshotLocked();
        }

        PublishPages(quests, force: true);
    }

    /// <summary>
    /// Страницы заданий: пропуск действует до смены задания, даже после выполнения;
    /// выделение и прокрутку расширение читает только у невыполненных заданий на них.
    /// </summary>
    private void PublishPages(IReadOnlyList<QuestView> quests, bool force)
    {
        var pages = new List<QuestPage>();
        foreach (var quest in quests)
        {
            if (quest.Url is not { } url || PageAddress.Normalize(url) is not { } page || !_templates.TryGetValue(quest.Template, out var template))
            {
                continue;
            }

            pages.Add(new QuestPage(
                quest.Id,
                page,
                url,
                Pass: true,
                WatchSelection: !quest.Completed && template.Type == QuestTypes.Select,
                WatchScroll: !quest.Completed && template.Type == QuestTypes.Read));
        }

        lock (_lock)
        {
            if (!force && _pages is not null && _pages.SequenceEqual(pages))
            {
                return;
            }

            _pages = pages;
        }

        _events.Publish(new QuestPagesChanged(pages));
    }

    private void Announce(List<QuestView> issued)
    {
        foreach (var quest in issued)
        {
            _events.Publish(new QuestIssued(quest, _options.Notify));
        }

        if (issued.Count > 0)
        {
            PublishState(Snapshot(), force: false);
        }
    }

    /// <summary>Задания для окна и строка HUD: первое невыполненное задание с прогрессом.</summary>
    private void PublishState(IReadOnlyList<QuestView> quests, bool force)
    {
        _events.Publish(new QuestsChanged(quests, Chain, ChainGoal));
        PublishPages(quests, force);
        var next = quests.FirstOrDefault(quest => !quest.Completed);
        var text = next is null ? null : $"{next.Title} {QuestFormat.Progress(next)}";
        lock (_lock)
        {
            if (!force && _hudPublished && text == _hudText)
            {
                return;
            }

            _hudText = text;
            _hudPublished = true;
        }

        _events.Publish(new HudStatusChanged(HudSlot.Quest, text));
    }

    /// <summary>Последний отчёт о медиаэлементе: позиция, когда пришла, играл ли со звуком, скорость.</summary>
    private sealed record Sample(double? Position, DateTimeOffset Time, bool Playing, double Rate);
}
