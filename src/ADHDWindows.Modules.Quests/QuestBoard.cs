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
    string? GameName = null)
{
    public QuestView View() => new(Id, Template, Tier, Title, Icon, Progress, Goal, Unit, Reward, Issued, Refresh, Completed);
}

/// <summary>
/// Доска заданий: одно задание на каждую сложность. Таймер сложности меняет задание на новое, выполнено оно или нет.
/// Задание выдаётся, только если его можно выполнить. Награда приходит ровно один раз.
/// Методы можно вызывать из любого потока, события рассылаются вне замка.
/// </summary>
public sealed class QuestBoard
{
    public const string ActiveKey = "active";

    /// <summary>Источник дохода наград за задания.</summary>
    public const string RewardSource = "quest";

    private const int RewardStep = 5;

    /// <summary>Если подходящих шаблонов нет, следующая попытка — через эту паузу, а не каждую секунду.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

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

    public QuestBoard(
        IReadOnlyList<QuestTemplate> templates,
        QuestOptions options,
        IEconomy economy,
        IEventBus events,
        IKeyValueStore saves,
        TextCatalog texts,
        TimeProvider clock,
        Random random,
        ILog log)
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

    /// <summary>Рассылает задания и строку HUD.</summary>
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

    private void Save() => _saves.Set(ActiveKey, _active.Values.OrderBy(state => state.Tier).ToList());

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
            .Replace("{game}", game?.Name ?? string.Empty, StringComparison.Ordinal);
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
            game?.Name);
        _active[tier] = state;
        _log.Info($"Выдано задание {chosen.Id} ({tier}): «{title}», награда {state.Reward}");
        return state.View();
    }

    private bool Feasible(QuestTemplate template, IReadOnlyList<GameInstall> playable) => template.Type switch
    {
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

    private void Advance(Func<QuestState, QuestTemplate, bool> counts, double amount)
    {
        var completed = new List<QuestState>();
        lock (_lock)
        {
            foreach (var state in _active.Values.ToList())
            {
                if (state.Completed || !_templates.TryGetValue(state.Template, out var template) || !counts(state, template))
                {
                    continue;
                }

                var progress = state.Progress + amount;
                if (progress >= state.Goal)
                {
                    // Отметка ставится под замком до начисления: второе событие уже не выдаст награду.
                    var done = state with { Progress = state.Goal, Completed = true };
                    _active[state.Tier] = done;
                    completed.Add(done);
                }
                else
                {
                    _active[state.Tier] = state with { Progress = progress };
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

        _events.Publish(new SaveRequested());
        PublishState(Snapshot(), force: false);
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
        _events.Publish(new QuestsChanged(quests));
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
}
