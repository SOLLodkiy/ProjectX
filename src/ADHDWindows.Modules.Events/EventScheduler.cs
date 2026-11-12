using ADHDWindows.Core.Data;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.RandomEvents;
using ADHDWindows.Core.Stats;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Modules.Events;

/// <summary>Событие-награда, которое ждёт своей очереди.</summary>
public sealed record QueuedEvent(string Id, string? Variant, string Reason);

/// <summary>Сколько раз каждое событие выпало случайно за местный день.</summary>
public sealed record EventDay(DateOnly Date, Dictionary<string, int> Started);

/// <summary>
/// Планировщик случайных событий (идея 011): раз в 15–30 минут одно случайное событие по весам из данных.
/// Одновременно идёт только одно событие. События не начинаются во время усложнения, предупреждения о нём, незадолго
/// до него и сразу после него, при «Не беспокоить» и в полноэкранных играх, на экранах UAC и при вводе пароля;
/// идущее событие с ресурсами (экран, окна) в таких случаях убирается. События-награды (сундук за цепочку заданий)
/// ждут в очереди и начинаются при первой возможности, даже если случайные события выключены.
/// Методы можно вызывать из любого потока; события шины рассылаются вне замка.
/// </summary>
public sealed class EventScheduler : IDisposable
{
    public const string FrequencySetting = "frequency";
    public const string DisabledSetting = "disabled";
    public const string NextSave = "next";
    public const string QueueSave = "queue";
    public const string DaySave = "day";
    public const int QueueLimit = 10;

    /// <summary>Вариант события, которое игрок открыл сам.</summary>
    public const string ManualVariant = RandomEventVariants.Manual;

    private readonly EventRules _rules;
    private readonly IEventBus _events;
    private readonly IKeyValueStore _saves;
    private readonly IKeyValueStore _settings;
    private readonly TextCatalog _texts;
    private readonly TimeProvider _clock;
    private readonly Random _random;
    private readonly ILog _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RandomEventInfo> _offered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disabled;
    private readonly HashSet<string> _skip = new(StringComparer.Ordinal);
    private readonly List<QueuedEvent> _queue;

    private RandomEventFrequency _frequency;
    private DateTimeOffset _next;
    private EventDay _day;
    private Running? _active;
    private string? _lastId;
    private string? _blocked;

    // Что известно об усложнениях из их шины.
    private bool _hardshipActive;
    private DateTimeOffset? _hardshipWarnedUntil;
    private DateTimeOffset? _hardshipNext;
    private DateTimeOffset? _hardshipEnded;
    private DateTimeOffset? _quietUntil;

    public EventScheduler(
        EventRules rules,
        IEventBus events,
        IKeyValueStore saves,
        IKeyValueStore settings,
        TextCatalog texts,
        TimeProvider clock,
        Random random,
        ILog log)
    {
        _rules = rules;
        _events = events;
        _saves = saves;
        _settings = settings;
        _texts = texts;
        _clock = clock;
        _random = random;
        _log = log;
        _disabled = (settings.Get<string[]>(DisabledSetting) ?? []).ToHashSet(StringComparer.Ordinal);
        _frequency = Enum.TryParse<RandomEventFrequency>(settings.Get<string>(FrequencySetting), true, out var frequency)
            ? frequency
            : RandomEventFrequency.Normal;
        _queue = [.. (saves.Get<List<QueuedEvent>>(QueueSave) ?? []).Where(item => !string.IsNullOrEmpty(item.Id)).Take(QueueLimit)];
        var now = clock.GetUtcNow();
        _day = saves.Get<EventDay>(DaySave) is { Started: not null } day && day.Date == Today(now)
            ? day
            : new EventDay(Today(now), new Dictionary<string, int>(StringComparer.Ordinal));

        var earliest = now + TimeSpan.FromMinutes(rules.FirstDelayMinutes);
        _next = saves.Get<DateTimeOffset?>(NextSave) is { } saved && saved > earliest && saved < now + Interval() * 2
            ? saved
            : Max(earliest, now + Delay());
        saves.Set(NextSave, _next);
    }

    public RandomEventFrequency Frequency
    {
        get
        {
            lock (_lock)
            {
                return _frequency;
            }
        }
    }

    public RandomEventInfo? Active
    {
        get
        {
            lock (_lock)
            {
                return _active?.Info;
            }
        }
    }

    public DateTimeOffset Next
    {
        get
        {
            lock (_lock)
            {
                return _next;
            }
        }
    }

    public IReadOnlyList<QueuedEvent> Queue
    {
        get
        {
            lock (_lock)
            {
                return [.. _queue];
            }
        }
    }

    public IReadOnlyCollection<RandomEventInfo> Offered
    {
        get
        {
            lock (_lock)
            {
                return [.. _offered.Values];
            }
        }
    }

    public void Offer(RandomEventInfo info)
    {
        lock (_lock)
        {
            _offered[info.Id] = info;
        }

        PublishStatus();
    }

    /// <summary>Новости усложнений: идёт ли усложнение, «Не беспокоить», когда следующее.</summary>
    public void OnHardshipStatus(HardshipStatus status)
    {
        Action? interrupt;
        lock (_lock)
        {
            _hardshipActive = status.Active is not null;
            _quietUntil = status.QuietUntil;
            _hardshipNext = status.Mode == HardshipMode.Off ? null : status.Next;
            interrupt = _hardshipActive ? Interrupt("hardship") : null;
        }

        interrupt?.Invoke();
    }

    public void OnHardshipWarned(HardshipWarned warned)
    {
        lock (_lock)
        {
            _hardshipWarnedUntil = _clock.GetUtcNow() + warned.Delay;
        }
    }

    /// <summary>Усложнение началось: события на экране уступают ему сразу.</summary>
    public void OnHardshipStarted()
    {
        Action? interrupt;
        lock (_lock)
        {
            _hardshipActive = true;
            _hardshipWarnedUntil = null;
            interrupt = Interrupt("hardship");
        }

        interrupt?.Invoke();
    }

    public void OnHardshipEnded()
    {
        lock (_lock)
        {
            _hardshipActive = false;
            _hardshipEnded = _clock.GetUtcNow();
        }
    }

    /// <summary>
    /// Шаг планировщика, раз в секунду. <paramref name="safety"/> — код ограничения безопасности
    /// (полноэкранное окно, UAC, пароль, созвон) или null.
    /// </summary>
    public void Tick(string? safety)
    {
        var now = _clock.GetUtcNow();
        var actions = new List<Action>();
        lock (_lock)
        {
            RollDay(now);
            var blocked = Blocked(now, safety);
            if (blocked != _blocked)
            {
                _blocked = blocked;
                actions.Add(PublishStatus);
            }

            if (_active is { } active)
            {
                if (blocked is not null && active.Info.Resources != HardshipResources.None)
                {
                    actions.Add(Finish(active, RandomEventEndReason.Interrupted, 0));
                }
                else if (now >= active.Ends)
                {
                    actions.Add(Finish(active, RandomEventEndReason.Missed, 0));
                }
            }
            else if (blocked is null)
            {
                if (_queue.FirstOrDefault(item => _offered.ContainsKey(item.Id) && !_skip.Contains(item.Id)) is { } queued)
                {
                    actions.Add(Launch(_offered[queued.Id], queued.Variant, now, queued, counted: false));
                }
                else if (now >= _next && _frequency != RandomEventFrequency.Off)
                {
                    if (Pick(now) is { } chosen)
                    {
                        actions.Add(Launch(chosen, null, now, null, counted: true));
                    }
                    else
                    {
                        // Ни одно событие сейчас недоступно: следующая попытка через обычный интервал.
                        _skip.Clear();
                        Reschedule(now + Delay());
                        actions.Add(PublishStatus);
                    }
                }
            }
            else if (now >= _next && _frequency != RandomEventFrequency.Off)
            {
                Reschedule(now + TimeSpan.FromMinutes(_rules.PostponeMinutes));
                _log.Info($"Событие отложено: {blocked}");
                actions.Add(PublishStatus);
            }
        }

        actions.ForEach(action => action());
    }

    /// <summary>Модуль закончил событие сам.</summary>
    public void OnFinished(RandomEventFinished finished)
    {
        Action? finish = null;
        lock (_lock)
        {
            if (_active is { } active && active.Info.Id == finished.Id)
            {
                finish = Finish(active, finished.Success ? RandomEventEndReason.Completed : RandomEventEndReason.Missed, finished.Reward);
            }
        }

        finish?.Invoke();
    }

    /// <summary>Модуль не смог начать событие: следующее пробуется на следующем шаге.</summary>
    public void OnFailed(RandomEventFailed failed)
    {
        var actions = new List<Action>();
        lock (_lock)
        {
            if (_active is not { } active || active.Info.Id != failed.Id)
            {
                return;
            }

            _active = null;
            active.EndReason = RandomEventEndReason.Failed;
            _skip.Add(failed.Id);
            if (active.Queued is { } queued && failed.Reason == "unavailable")
            {
                _queue.Remove(queued);
                SaveQueue();
            }

            if (failed.Reason != "unavailable")
            {
                _log.Warn($"Событие не началось: {failed.Id} ({failed.Reason})");
            }

            var info = active.Info;
            actions.Add(() => _events.Publish(new RandomEventStopRequested(info.Id)));
            if (active.Announced)
            {
                actions.Add(() => _events.Publish(new RandomEventEnded(info, RandomEventEndReason.Failed, 0)));
            }

            actions.Add(PublishStatus);
        }

        actions.ForEach(action => action());
    }

    /// <summary>Событие-награда в очередь.</summary>
    public void Enqueue(RandomEventQueueRequested request)
    {
        lock (_lock)
        {
            if (_queue.Count >= QueueLimit)
            {
                _log.Warn($"Очередь событий заполнена, пропущено: {request.Id} ({request.Reason})");
                return;
            }

            _queue.Add(new QueuedEvent(request.Id, request.Variant, request.Reason));
            SaveQueue();
            _log.Info($"Событие в очереди: {request.Id} ({request.Reason})");
        }
    }

    /// <summary>Игрок сам открыл событие, например колесо фортуны. Не во время усложнения и другого события.</summary>
    public bool Open(string id)
    {
        var now = _clock.GetUtcNow();
        Action launch;
        lock (_lock)
        {
            if (!_offered.TryGetValue(id, out var info) || !info.Manual || _active is not null || _hardshipActive)
            {
                _log.Info($"Событие нельзя открыть сейчас: {id}");
                return false;
            }

            launch = Launch(info, ManualVariant, now, null, counted: false);
        }

        launch();
        return Active?.Id == id;
    }

    /// <summary>Режим разработчика: начать событие сейчас, уважая ограничения. Null — случайное.</summary>
    public bool Test(string? id, string? variant, string? safety)
    {
        var now = _clock.GetUtcNow();
        var actions = new List<Action>();
        RandomEventInfo? chosen;
        lock (_lock)
        {
            if (Blocked(now, safety) is { } blocked)
            {
                _log.Warn($"Тестовый запуск события запрещён: {blocked}");
                return false;
            }

            if (_active is { } active)
            {
                actions.Add(Finish(active, RandomEventEndReason.Cancelled, 0));
            }

            chosen = id is null
                ? Choose([.. _offered.Values.Where(info => !_disabled.Contains(info.Id))], weighted: false)
                : _offered.GetValueOrDefault(id);
        }

        actions.ForEach(action => action());
        if (chosen is null)
        {
            _log.Warn($"Нет события для тестового запуска: {id ?? "любое"}");
            return false;
        }

        Action launch;
        lock (_lock)
        {
            if (_active is not null)
            {
                return false;
            }

            launch = Launch(chosen, variant, now, null, counted: false);
        }

        launch();
        return Active?.Id == chosen.Id;
    }

    /// <summary>Убирает идущее событие без награды.</summary>
    public void Cancel()
    {
        Action? finish = null;
        lock (_lock)
        {
            if (_active is { } active)
            {
                finish = Finish(active, RandomEventEndReason.Cancelled, 0);
            }
        }

        finish?.Invoke();
    }

    public void SetFrequency(RandomEventFrequency frequency)
    {
        lock (_lock)
        {
            if (frequency == _frequency)
            {
                return;
            }

            _frequency = frequency;
            _settings.Set(FrequencySetting, frequency.ToString().ToLowerInvariant());
            Reschedule(_clock.GetUtcNow() + Delay());
            _log.Info($"Частота событий: {frequency}");
        }

        PublishStatus();
    }

    public void SetEnabled(string id, bool enabled)
    {
        Action? finish = null;
        lock (_lock)
        {
            if (enabled ? !_disabled.Remove(id) : !_disabled.Add(id))
            {
                return;
            }

            _settings.Set(DisabledSetting, _disabled.Order(StringComparer.Ordinal).ToArray());
            if (!enabled && _active is { } active && active.Info.Id == id)
            {
                finish = Finish(active, RandomEventEndReason.Cancelled, 0);
            }
        }

        finish?.Invoke();
        PublishStatus();
    }

    public void PublishStatus()
    {
        RandomEventStatus status;
        lock (_lock)
        {
            status = new RandomEventStatus(
                _frequency,
                [.. _offered.Values.OrderBy(info => info.Id, StringComparer.Ordinal).Select(info => new RandomEventEntry(info, !_disabled.Contains(info.Id)))],
                _active?.Info,
                _frequency == RandomEventFrequency.Off ? null : _next,
                _blocked);
        }

        _events.Publish(status);
    }

    public void Dispose()
    {
        string? id;
        lock (_lock)
        {
            id = _active?.Info.Id;
            _active = null;
        }

        if (id is not null)
        {
            _events.Publish(new RandomEventStopRequested(id));
        }
    }

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.ToLocalTime().DateTime);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private string? Blocked(DateTimeOffset now, string? safety)
    {
        if (safety is not null)
        {
            return safety;
        }

        if (_quietUntil is { } quiet && quiet > now)
        {
            return "quiet";
        }

        var gap = TimeSpan.FromSeconds(_rules.HardshipGapSeconds);
        if (_hardshipActive
            || (_hardshipWarnedUntil is { } warned && warned + gap > now)
            || (_hardshipEnded is { } ended && ended + gap > now))
        {
            return "hardship";
        }

        // Событие не начинается перед самым усложнением, чтобы не оборваться на середине.
        return _hardshipNext is { } next && next > now && next - now < gap ? "hardshipSoon" : null;
    }

    private void RollDay(DateTimeOffset now)
    {
        if (_day.Date != Today(now))
        {
            _day = new EventDay(Today(now), new Dictionary<string, int>(StringComparer.Ordinal));
            _saves.Set(DaySave, _day);
        }
    }

    private RandomEventInfo? Pick(DateTimeOffset now)
    {
        var candidates = _offered.Values
            .Where(info => !_disabled.Contains(info.Id) && !_skip.Contains(info.Id))
            .Where(info => _rules.For(info.Id) is var rule && rule.Weight > 0
                && (rule.PerDay == 0 || _day.Started.GetValueOrDefault(info.Id) < rule.PerDay))
            .ToList();

        // Одно и то же событие два раза подряд не выпадает, если есть другие.
        if (candidates.Count > 1)
        {
            candidates.RemoveAll(info => info.Id == _lastId);
        }

        return Choose(candidates, weighted: true);
    }

    private RandomEventInfo? Choose(List<RandomEventInfo> candidates, bool weighted)
    {
        var weights = candidates.Select(info => weighted ? _rules.For(info.Id).Weight : 1).ToList();
        var total = weights.Sum();
        if (total <= 0)
        {
            return null;
        }

        var roll = _random.NextDouble() * total;
        for (var index = 0; index < candidates.Count; index++)
        {
            roll -= weights[index];
            if (roll < 0 && weights[index] > 0)
            {
                return candidates[index];
            }
        }

        return candidates.Where((_, index) => weights[index] > 0).Last();
    }

    /// <summary>Просит модуль начать событие. Вызывается под замком, действие — вне его.</summary>
    private Action Launch(RandomEventInfo info, string? variant, DateTimeOffset now, QueuedEvent? queued, bool counted)
    {
        var ends = now + TimeSpan.FromSeconds(_rules.For(info.Id).Seconds);
        var running = new Running(info, variant, ends, queued);
        _active = running;
        if (counted)
        {
            Reschedule(now + Delay());
        }

        return () =>
        {
            _events.Publish(new RandomEventStartRequested(info.Id, variant, ends));
            lock (_lock)
            {
                // Модуль мог отказаться сразу или закончить мгновенное событие раньше, чем о его начале объявлено.
                if (running.EndReason == RandomEventEndReason.Failed || (running.EndReason is null && _active != running))
                {
                    return;
                }

                running.Announced = true;
                _skip.Clear();
                if (counted)
                {
                    _lastId = info.Id;
                    _day.Started[info.Id] = _day.Started.GetValueOrDefault(info.Id) + 1;
                    _saves.Set(DaySave, _day);
                }
            }

            _events.Publish(new RandomEventStarted(info, variant, ends));
            if (running.EndReason is { } reason)
            {
                AnnounceEnd(info, reason, running.Reward);
            }

            PublishStatus();
        };
    }

    /// <summary>Убирает событие. Вызывается под замком, действие — вне его.</summary>
    private Action Finish(Running active, RandomEventEndReason reason, long reward)
    {
        _active = null;
        if (active.Queued is { } queued && reason is RandomEventEndReason.Completed or RandomEventEndReason.Missed)
        {
            _queue.Remove(queued);
            SaveQueue();
        }

        if (reason != RandomEventEndReason.Completed)
        {
            _log.Info($"Событие закончилось: {active.Info.Id} ({reason})");
        }

        active.EndReason = reason;
        active.Reward = reward;
        var info = active.Info;
        var announced = active.Announced;
        return () =>
        {
            _events.Publish(new RandomEventStopRequested(info.Id));
            if (announced)
            {
                AnnounceEnd(info, reason, reward);
            }
        };
    }

    private void AnnounceEnd(RandomEventInfo info, RandomEventEndReason reason, long reward)
    {
        _events.Publish(new RandomEventEnded(info, reason, reward));
        if (reason == RandomEventEndReason.Completed)
        {
            _log.Info($"Событие пройдено: {info.Id}, награда {reward}");
            _events.Publish(new StatCounted($"events.{info.Id}"));
            _events.Publish(new StatCounted("events.completed"));
            _events.Publish(new FeedPosted("star", _texts.Get(reward > 0 ? "events.feed.reward" : "events.feed.done", _texts.Get($"events.{info.Id}.title"), reward)));
        }

        PublishStatus();
    }

    private Action? Interrupt(string reason)
    {
        if (_active is not { } active || active.Info.Resources == HardshipResources.None)
        {
            return null;
        }

        _log.Info($"Событие уступает: {reason}");
        return Finish(active, RandomEventEndReason.Interrupted, 0);
    }

    private TimeSpan Interval()
    {
        var frequency = _rules.FrequencyOf(_frequency);
        return TimeSpan.FromMinutes(_rules.IntervalMinutes / (frequency > 0 ? frequency : 1));
    }

    /// <summary>Интервал с разбросом: 22,5 ± 7,5 минуты при обычной частоте.</summary>
    private TimeSpan Delay()
    {
        var frequency = _rules.FrequencyOf(_frequency);
        var scale = frequency > 0 ? 1 / frequency : 1;
        var minutes = _rules.IntervalMinutes + ((_random.NextDouble() * 2) - 1) * _rules.SpreadMinutes;
        return TimeSpan.FromMinutes(minutes * scale);
    }

    private void Reschedule(DateTimeOffset next)
    {
        _next = next;
        _saves.Set(NextSave, next);
    }

    private void SaveQueue() => _saves.Set(QueueSave, _queue.ToList());

    private sealed class Running(RandomEventInfo info, string? variant, DateTimeOffset ends, QueuedEvent? queued)
    {
        public RandomEventInfo Info { get; } = info;

        public string? Variant { get; } = variant;

        public DateTimeOffset Ends { get; } = ends;

        public QueuedEvent? Queued { get; } = queued;

        /// <summary>Модуль принял событие, и о его начале уже объявлено.</summary>
        public bool Announced { get; set; }

        /// <summary>Как событие закончилось, null — ещё идёт.</summary>
        public RandomEventEndReason? EndReason { get; set; }

        public long Reward { get; set; }
    }
}
