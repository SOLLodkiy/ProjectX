using System.Globalization;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Modules.Hardships;

/// <summary>
/// Планировщик усложнений: раз в интервал с разбросом предупреждает, затем включает одно усложнение и снимает его
/// по истечении времени. Режим задаёт множитель всего дохода. Ограничения безопасности и «Не беспокоить» откладывают
/// усложнение, а работающее снимают. Методы можно вызывать из любого потока; события рассылаются вне замка.
/// </summary>
public sealed class HardshipScheduler : IDisposable
{
    public const string BonusSource = "hardship";

    public const string ModeSetting = "mode";
    public const string DisabledSetting = "disabled";
    public const string IntervalSetting = "interval";
    public const string PostponeSetting = "postpone";
    public const string NextSave = "next";
    public const string QuietSave = "quietUntil";

    /// <summary>Повтор после усложнения, которое не смогло включиться.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    private readonly HardshipRules _rules;
    private readonly IEconomy _economy;
    private readonly IEventBus _events;
    private readonly IKeyValueStore _saves;
    private readonly IKeyValueStore _settings;
    private readonly TextCatalog _texts;
    private readonly TimeProvider _clock;
    private readonly Random _random;
    private readonly ILog _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, HardshipInfo> _offered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disabled;
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    private HardshipMode _mode;
    private IDisposable? _modifier;
    private DateTimeOffset _next;
    private DateTimeOffset? _quietUntil;
    private HardshipInfo? _warned;
    private DateTimeOffset _startsAt;
    private HardshipInfo? _active;
    private DateTimeOffset _endsAt;
    private string? _lastId;
    private string? _blocked;
    private string? _hudText;
    private bool _hudPublished;

    public HardshipScheduler(
        HardshipRules rules,
        IEconomy economy,
        IEventBus events,
        IKeyValueStore saves,
        IKeyValueStore settings,
        TextCatalog texts,
        TimeProvider clock,
        Random random,
        ILog log)
    {
        _rules = rules;
        _economy = economy;
        _events = events;
        _saves = saves;
        _settings = settings;
        _texts = texts;
        _clock = clock;
        _random = random;
        _log = log;
        _disabled = (settings.Get<string[]>(DisabledSetting) ?? []).ToHashSet(StringComparer.Ordinal);
        _mode = ParseMode(settings.Get<string>(ModeSetting));
        _modifier = economy.AddModifier(new IncomeModifier(IncomeStep.Hardship, rules.For(_mode).Multiplier));

        var now = clock.GetUtcNow();
        var saved = saves.Get<DateTimeOffset?>(NextSave);
        var earliest = now + TimeSpan.FromMinutes(rules.FirstDelayMinutes);

        // Перезапуск не отменяет запланированное усложнение, но и не запускает его сразу после старта.
        _next = saved is { } next && next > earliest && next < now + Interval() * 2 ? next : Max(earliest, now + Delay());
        _quietUntil = saves.Get<DateTimeOffset?>(QuietSave) is { } quiet && quiet > now ? quiet : null;
        saves.Set(NextSave, _next);
    }

    public HardshipMode Mode
    {
        get
        {
            lock (_lock)
            {
                return _mode;
            }
        }
    }

    public HardshipInfo? Active
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    public HardshipInfo? Warned
    {
        get
        {
            lock (_lock)
            {
                return _warned;
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

    public IReadOnlyCollection<HardshipInfo> Offered
    {
        get
        {
            lock (_lock)
            {
                return [.. _offered.Values];
            }
        }
    }

    public void Offer(HardshipInfo info)
    {
        lock (_lock)
        {
            _offered[info.Id] = info;
        }

        PublishStatus();
    }

    /// <summary>
    /// Шаг планировщика, вызывается раз в секунду. <paramref name="blocked"/> — код причины, по которой сейчас
    /// усложнения нельзя запускать (полноэкранная игра, UAC, пароль, созвон), или null.
    /// </summary>
    public void Tick(string? blocked)
    {
        var now = _clock.GetUtcNow();
        var actions = new List<Action>();
        lock (_lock)
        {
            if (_quietUntil is { } quiet && quiet <= now)
            {
                _quietUntil = null;
                _saves.Remove(QuietSave);
                actions.Add(PublishStatus);
            }

            blocked ??= _quietUntil is null ? null : "quiet";
            if (blocked != _blocked)
            {
                _blocked = blocked;
                actions.Add(PublishStatus);
            }

            if (_active is { } active)
            {
                if (blocked is not null)
                {
                    actions.Add(Finish(active, HardshipEndReason.Interrupted, now));
                }
                else if (now >= _endsAt)
                {
                    actions.Add(Finish(active, HardshipEndReason.Survived, now));
                }
            }
            else if (_warned is { } warned)
            {
                if (blocked is not null || !CanRun(warned))
                {
                    _warned = null;
                    Reschedule(now + Postpone());
                    _log.Info($"Усложнение отложено ({blocked ?? "unavailable"}): {warned.Id}");
                    actions.Add(PublishStatus);
                }
                else if (now >= _startsAt)
                {
                    _warned = null;
                    actions.Add(() => Launch(warned, now));
                }
            }
            else if (now >= _next)
            {
                if (_mode == HardshipMode.Off)
                {
                    Reschedule(now + Delay());
                }
                else if (blocked is not null)
                {
                    Reschedule(now + Postpone());
                    _log.Info($"Усложнение отложено: {blocked}");
                    actions.Add(PublishStatus);
                }
                else if (Pick() is { } chosen)
                {
                    _warned = chosen;
                    _startsAt = now + TimeSpan.FromSeconds(_rules.WarningSeconds);
                    var delay = _startsAt - now;
                    actions.Add(() => _events.Publish(new HardshipWarned(chosen, delay)));
                    actions.Add(PublishStatus);
                }
                else
                {
                    Reschedule(now + Delay());
                }
            }
        }

        actions.ForEach(action => action());
        PublishHud(now);
    }

    /// <summary>Режим разработчика: запускает усложнение сейчас, без предупреждения. Null — случайное из доступных.</summary>
    public bool Test(string? id, string? blocked)
    {
        var now = _clock.GetUtcNow();
        HardshipInfo? chosen;
        var actions = new List<Action>();
        lock (_lock)
        {
            if (blocked is not null)
            {
                _log.Warn($"Тестовый запуск усложнения запрещён: {blocked}");
                return false;
            }

            if (_active is { } active)
            {
                actions.Add(Finish(active, HardshipEndReason.Cancelled, now));
            }

            _warned = null;
            chosen = id is null ? PickAny() : _offered.GetValueOrDefault(id);
        }

        actions.ForEach(action => action());
        if (chosen is null)
        {
            _log.Warn($"Нет усложнения для тестового запуска: {id ?? "любое"}");
            return false;
        }

        Launch(chosen, now);
        PublishHud(now);
        return Active?.Id == chosen.Id;
    }

    /// <summary>Снимает работающее усложнение без бонуса или отменяет предупреждение.</summary>
    public void Cancel()
    {
        var now = _clock.GetUtcNow();
        Action? finish = null;
        lock (_lock)
        {
            if (_active is { } active)
            {
                finish = Finish(active, HardshipEndReason.Cancelled, now);
            }
            else if (_warned is not null)
            {
                _warned = null;
                Reschedule(now + Delay());
            }
        }

        finish?.Invoke();
        PublishStatus();
        PublishHud(now);
    }

    public void SetMode(HardshipMode mode)
    {
        var now = _clock.GetUtcNow();
        Action? finish = null;
        lock (_lock)
        {
            if (mode == _mode)
            {
                return;
            }

            _mode = mode;
            _settings.Set(ModeSetting, mode.ToString().ToLowerInvariant());
            _modifier?.Dispose();
            _modifier = _economy.AddModifier(new IncomeModifier(IncomeStep.Hardship, _rules.For(mode).Multiplier));
            if (_warned is { } warned && !CanRun(warned))
            {
                _warned = null;
            }

            if (_active is { } active && !CanRun(active))
            {
                finish = Finish(active, HardshipEndReason.Cancelled, now);
            }

            // Новый режим меняет частоту, поэтому следующее усложнение планируется заново.
            if (_active is null && _warned is null)
            {
                Reschedule(now + Delay());
            }

            _log.Info($"Режим усложнений: {mode}");
        }

        finish?.Invoke();
        PublishStatus();
        PublishHud(now);
    }

    public void SetEnabled(string id, bool enabled)
    {
        var now = _clock.GetUtcNow();
        Action? finish = null;
        lock (_lock)
        {
            var changed = enabled ? _disabled.Remove(id) : _disabled.Add(id);
            if (!changed)
            {
                return;
            }

            _settings.Set(DisabledSetting, _disabled.Order(StringComparer.Ordinal).ToArray());
            if (!enabled && _active?.Id == id)
            {
                finish = Finish(_active, HardshipEndReason.Cancelled, now);
            }

            if (!enabled && _warned?.Id == id)
            {
                _warned = null;
                Reschedule(now + Postpone());
            }
        }

        finish?.Invoke();
        PublishStatus();
        PublishHud(now);
    }

    /// <summary>«Не беспокоить» на время. Ноль выключает режим.</summary>
    public void SetQuiet(TimeSpan duration)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (duration <= TimeSpan.Zero)
            {
                _quietUntil = null;
                _saves.Remove(QuietSave);
            }
            else
            {
                _quietUntil = now + duration;
                _saves.Set(QuietSave, _quietUntil.Value);
            }
        }

        // Работающее усложнение снимет ближайший шаг, как при любом другом ограничении.
        Tick(null);
        PublishStatus();
    }

    /// <summary>Модуль усложнения сообщил, что не смог его включить.</summary>
    public void OnFailed(HardshipFailed failed)
    {
        lock (_lock)
        {
            _failed.Add(failed.Id);
        }
    }

    public void PublishStatus()
    {
        HardshipStatus status;
        lock (_lock)
        {
            var entries = _offered.Values
                .OrderBy(info => info.Level)
                .ThenBy(info => info.Id, StringComparer.Ordinal)
                .Select(info => new HardshipEntry(info, !_disabled.Contains(info.Id)))
                .ToList();
            status = new HardshipStatus(
                _mode,
                _rules.For(_mode).Multiplier,
                entries,
                _active,
                _active is null ? null : _endsAt,
                _mode == HardshipMode.Off ? null : _warned is null ? _next : _startsAt,
                _quietUntil,
                _blocked);
        }

        _events.Publish(status);
    }

    /// <summary>Снимает работающее усложнение при остановке программы и убирает правку дохода.</summary>
    public void Dispose()
    {
        Action? finish = null;
        lock (_lock)
        {
            if (_active is { } active)
            {
                finish = Finish(active, HardshipEndReason.Cancelled, _clock.GetUtcNow());
            }

            _modifier?.Dispose();
            _modifier = null;
        }

        finish?.Invoke();
    }

    internal static HardshipMode ParseMode(string? value) =>
        Enum.TryParse<HardshipMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode) ? mode : HardshipMode.Normal;

    private void Launch(HardshipInfo hardship, DateTimeOffset now)
    {
        lock (_lock)
        {
            _failed.Remove(hardship.Id);
        }

        // Модуль усложнения отвечает синхронно: ошибку включения он присылает в этом же вызове.
        _events.Publish(new HardshipStartRequested(hardship.Id));
        bool failed;
        lock (_lock)
        {
            failed = _failed.Remove(hardship.Id);
            if (failed)
            {
                Reschedule(now + RetryAfterFailure);
            }
            else
            {
                _active = hardship;
                _lastId = hardship.Id;
                _endsAt = now + Duration(hardship.Level);
            }
        }

        if (failed)
        {
            _events.Publish(new HardshipStopRequested(hardship.Id));
            _events.Publish(new HardshipEnded(hardship, HardshipEndReason.Failed, 0));
            _log.Warn($"Усложнение не включилось: {hardship.Id}");
        }
        else
        {
            _log.Info($"Усложнение началось: {hardship.Id} до {_endsAt:HH:mm:ss}");
            _events.Publish(new HardshipStarted(hardship, _endsAt));
        }

        PublishStatus();
    }

    /// <summary>Меняет состояние под замком и возвращает рассылку событий, которую нужно выполнить вне замка.</summary>
    private Action Finish(HardshipInfo hardship, HardshipEndReason reason, DateTimeOffset now)
    {
        _active = null;
        Reschedule(now + (reason == HardshipEndReason.Interrupted ? Postpone() : Delay()));
        var bonus = reason == HardshipEndReason.Survived ? _rules.For(hardship.Level).Bonus : 0;
        return () =>
        {
            _events.Publish(new HardshipStopRequested(hardship.Id));
            var earned = bonus > 0 ? _economy.Earn(BonusSource, bonus) : 0;
            _log.Info($"Усложнение закончилось: {hardship.Id}, {reason}, бонус {earned}");
            _events.Publish(new HardshipEnded(hardship, reason, earned));
            PublishStatus();
        };
    }

    private bool CanRun(HardshipInfo hardship) =>
        _mode != HardshipMode.Off
        && !_disabled.Contains(hardship.Id)
        && _offered.ContainsKey(hardship.Id)
        && _rules.For(_mode).Weights[(int)hardship.Level] > 0;

    private HardshipInfo? Pick()
    {
        var candidates = _offered.Values.Where(CanRun).ToList();

        // Одно и то же усложнение два раза подряд надоедает, если есть другие.
        if (candidates.Count > 1)
        {
            candidates.RemoveAll(info => info.Id == _lastId);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var weights = _rules.For(_mode).Weights;

        // Вес уровня делится между его усложнениями: частота уровня не зависит от того, сколько их в нём.
        var perLevel = candidates.GroupBy(info => info.Level).ToDictionary(group => group.Key, group => group.Count());
        var total = candidates.Sum(info => weights[(int)info.Level] / perLevel[info.Level]);
        var roll = _random.NextDouble() * total;
        foreach (var info in candidates.OrderBy(info => info.Id, StringComparer.Ordinal))
        {
            roll -= weights[(int)info.Level] / perLevel[info.Level];
            if (roll < 0)
            {
                return info;
            }
        }

        return candidates[^1];
    }

    private HardshipInfo? PickAny()
    {
        var candidates = _offered.Values.Where(info => !_disabled.Contains(info.Id)).ToList();
        return candidates.Count == 0 ? null : candidates[_random.Next(candidates.Count)];
    }

    private void Reschedule(DateTimeOffset next)
    {
        _next = next;
        _saves.Set(NextSave, next);
    }

    private TimeSpan Interval()
    {
        var minutes = _settings.Get<double?>(IntervalSetting) is > 0 and < 24 * 60 and var custom ? custom : _rules.IntervalMinutes;
        var frequency = _rules.For(_mode).Frequency;
        return TimeSpan.FromMinutes(frequency > 0 ? minutes / frequency : minutes);
    }

    /// <summary>Интервал со случайным разбросом.</summary>
    private TimeSpan Delay()
    {
        var interval = Interval();
        var spread = TimeSpan.FromMinutes(_rules.SpreadMinutes * interval.TotalMinutes / Math.Max(_rules.IntervalMinutes, 1));
        var delay = interval + spread * (_random.NextDouble() * 2 - 1);
        return delay < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay;
    }

    private TimeSpan Postpone() =>
        TimeSpan.FromMinutes(_settings.Get<double?>(PostponeSetting) is > 0 and < 24 * 60 and var custom ? custom : _rules.PostponeMinutes);

    private TimeSpan Duration(HardshipLevel level)
    {
        var rules = _rules.For(level);
        return TimeSpan.FromSeconds(rules.Min + (_random.NextDouble() * (rules.Max - rules.Min)));
    }

    private void PublishHud(DateTimeOffset now)
    {
        string? text;
        lock (_lock)
        {
            text = _active is { } active
                ? $"{Title(active)} {Clock(_endsAt - now)}"
                : _warned is { } warned
                    ? _texts.Get("hardships.hud.soon", Title(warned), Clock(_startsAt - now))
                    : _quietUntil is not null ? _texts.Get("hardships.hud.quiet") : null;
            if (_hudPublished && text == _hudText)
            {
                return;
            }

            _hudText = text;
            _hudPublished = true;
        }

        _events.Publish(new HudStatusChanged(HudSlot.Hardship, text));
    }

    private string Title(HardshipInfo info) => _texts.Get($"hardships.{info.Id}.title");

    private static string Clock(TimeSpan left)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
