using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Stats;
using ADHDWindows.Core.Storage;
using ADHDWindows.Core.Upgrades;

namespace ADHDWindows.Modules.Stats;

/// <summary>Итоги дня в сохранении.</summary>
public sealed class DayData
{
    public long Earned { get; set; }
    public long Spent { get; set; }
    public long Clicks { get; set; }
    public double MediaSeconds { get; set; }
}

/// <summary>Вся статистика в сохранении. Хранится только в файле сохранения на этом компьютере.</summary>
public sealed class StatsState
{
    public DateTimeOffset Since { get; set; }
    public long TotalEarned { get; set; }
    public long TotalSpent { get; set; }
    public string? BestDay { get; set; }
    public long BestDayEarned { get; set; }
    public Dictionary<string, long> EarnedBySource { get; set; } = [];
    public Dictionary<string, long> Clicks { get; set; } = [];
    public int BestClicksPerSecond { get; set; }
    public double CursorPixels { get; set; }
    public double AppSeconds { get; set; }
    public Dictionary<string, double> MediaSeconds { get; set; } = [];
    public Dictionary<string, double> Sites { get; set; } = [];
    public Dictionary<string, double> Programs { get; set; } = [];
    public Dictionary<string, double> Games { get; set; } = [];
    public List<UpgradeRecord> Upgrades { get; set; } = [];
    public Dictionary<string, long> Counters { get; set; } = [];

    /// <summary>По местной дате в виде yyyy-MM-dd.</summary>
    public Dictionary<string, DayData> Days { get; set; } = [];

    public List<FeedEntry> Feed { get; set; } = [];
}

/// <summary>
/// Собирает статистику из событий игры. Методы можно вызывать из любого потока.
/// Даты дней местные: день игрока кончается в его полночь.
/// </summary>
public sealed class StatsRecorder
{
    public const string SaveKey = "state";

    /// <summary>Пиксельный «метр»: 3780 точек, столько в метре экрана при 96 точках на дюйм.</summary>
    public const double PixelsPerMeter = 3780;

    public const int FeedLimit = 200;
    public const int TopCount = 5;

    /// <summary>Столько дней хранится для графиков: больше года.</summary>
    public const int DayLimit = 400;

    /// <summary>Столько сайтов, программ и игр хранится: редкие вытесняются.</summary>
    public const int UsageLimit = 300;

    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);

    private readonly IKeyValueStore _saves;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly StatsState _state;
    private readonly Queue<TimeSpan> _lastSecond = new();
    private CollectionChanged _collection = new([], []);

    public StatsRecorder(IKeyValueStore saves, TimeProvider clock)
    {
        _saves = saves;
        _clock = clock;
        _state = saves.Get<StatsState>(SaveKey) ?? new StatsState { Since = clock.GetUtcNow() };
    }

    public void OnEarned(CoinsEarned earned) => Change(state =>
    {
        state.TotalEarned = Add(state.TotalEarned, earned.Amount);
        state.EarnedBySource[earned.Source] = Add(state.EarnedBySource.GetValueOrDefault(earned.Source), earned.Amount);
        var (key, day) = Today(state);
        day.Earned = Add(day.Earned, earned.Amount);
        if (day.Earned > state.BestDayEarned)
        {
            state.BestDayEarned = day.Earned;
            state.BestDay = key;
        }
    });

    public void OnSpent(CoinsSpent spent) => Change(state =>
    {
        state.TotalSpent = Add(state.TotalSpent, spent.Amount);
        var day = Today(state).Day;
        day.Spent = Add(day.Spent, spent.Amount);
    });

    public void OnClick(ClickCounted click) => Change(state =>
    {
        var button = ButtonCode(click.Button);
        state.Clicks[button] = state.Clicks.GetValueOrDefault(button) + 1;
        Today(state).Day.Clicks++;

        while (_lastSecond.Count > 0 && click.Time - _lastSecond.Peek() >= RateWindow)
        {
            _lastSecond.Dequeue();
        }

        _lastSecond.Enqueue(click.Time);
        state.BestClicksPerSecond = Math.Max(state.BestClicksPerSecond, _lastSecond.Count);
    });

    public void OnMedia(MediaTimeCounted counted)
    {
        if (counted.Seconds <= 0 || !double.IsFinite(counted.Seconds))
        {
            return;
        }

        Change(state =>
        {
            state.MediaSeconds[counted.Source] = state.MediaSeconds.GetValueOrDefault(counted.Source) + counted.Seconds;
            Today(state).Day.MediaSeconds += counted.Seconds;
            Use(counted.Source == "game" ? state.Games : state.Sites, counted.Subject, counted.Seconds);
        });
    }

    public void OnUpgrade(UpgradePurchased purchased, string feedText) => Change(state =>
    {
        state.Upgrades.Add(new UpgradeRecord(purchased.Id, purchased.Name, purchased.Level, purchased.Price, purchased.When));
        Post(state, "arrow_up", feedText);
    });

    public void OnCounter(StatCounted counted) => Change(state =>
        state.Counters[counted.Key] = Add(state.Counters.GetValueOrDefault(counted.Key), counted.Amount));

    public void OnCollection(CollectionChanged collection)
    {
        lock (_lock)
        {
            _collection = collection;
        }
    }

    public void Post(string icon, string text) => Change(state => Post(state, icon, text));

    public void AddCursor(double pixels)
    {
        if (pixels > 0 && double.IsFinite(pixels))
        {
            Change(state => state.CursorPixels += pixels);
        }
    }

    public void AddAppTime(double seconds)
    {
        if (seconds > 0 && double.IsFinite(seconds))
        {
            Change(state => state.AppSeconds += seconds);
        }
    }

    public void AddProgram(string name, double seconds)
    {
        if (seconds > 0 && !string.IsNullOrWhiteSpace(name))
        {
            Change(state => Use(state.Programs, name, seconds));
        }
    }

    /// <summary>Записывает статистику в сохранение. На диск она попадёт с ближайшим сохранением ядра.</summary>
    public void Save()
    {
        lock (_lock)
        {
            _saves.Set(SaveKey, _state);
        }
    }

    public StatsSnapshot Snapshot(long balance)
    {
        lock (_lock)
        {
            var state = _state;
            DayStats? best = state.BestDay is { } key && DateOnly.TryParse(key, System.Globalization.CultureInfo.InvariantCulture, out var date)
                ? ToDay(date, state.Days.GetValueOrDefault(key) ?? new DayData { Earned = state.BestDayEarned })
                : null;
            var days = state.Days
                .Select(pair => DateOnly.TryParse(pair.Key, System.Globalization.CultureInfo.InvariantCulture, out var day) ? ToDay(day, pair.Value) : null)
                .OfType<DayStats>()
                .OrderBy(day => day.Date)
                .ToList();
            return new StatsSnapshot(
                _clock.GetUtcNow(),
                state.Since,
                balance,
                state.TotalEarned,
                state.TotalSpent,
                best,
                new Dictionary<string, long>(state.EarnedBySource),
                new Dictionary<string, long>(state.Clicks),
                state.BestClicksPerSecond,
                state.CursorPixels / PixelsPerMeter,
                state.AppSeconds,
                new Dictionary<string, double>(state.MediaSeconds),
                Top(state.Sites),
                Top(state.Programs),
                Top(state.Games),
                _collection.Items,
                _collection.Catalog,
                [.. state.Upgrades.OrderByDescending(upgrade => upgrade.When)],
                new Dictionary<string, long>(state.Counters),
                days,
                [.. Enumerable.Reverse(state.Feed)]);
        }
    }

    public static string ButtonCode(MouseButton button) => button switch
    {
        MouseButton.Left => "left",
        MouseButton.Right => "right",
        _ => "middle",
    };

    private void Change(Action<StatsState> change)
    {
        lock (_lock)
        {
            change(_state);
        }
    }

    private (string Key, DayData Day) Today(StatsState state)
    {
        var key = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (!state.Days.TryGetValue(key, out var day))
        {
            state.Days[key] = day = new DayData();

            // Ключи вида yyyy-MM-dd сравниваются как строки в порядке дат.
            foreach (var old in state.Days.Keys.Order(StringComparer.Ordinal).Take(Math.Max(0, state.Days.Count - DayLimit)).ToList())
            {
                state.Days.Remove(old);
            }
        }

        return (key, day);
    }

    private void Post(StatsState state, string icon, string text)
    {
        state.Feed.Add(new FeedEntry(_clock.GetUtcNow(), icon, text));
        if (state.Feed.Count > FeedLimit)
        {
            state.Feed.RemoveRange(0, state.Feed.Count - FeedLimit);
        }
    }

    private static void Use(Dictionary<string, double> usage, string name, double seconds)
    {
        usage[name] = usage.GetValueOrDefault(name) + seconds;
        if (usage.Count > UsageLimit)
        {
            // Только что добавленное не вытесняется: иначе новое никогда не накопило бы времени.
            usage.Remove(usage.Where(pair => pair.Key != name).MinBy(pair => pair.Value).Key);
        }
    }

    private static List<UsageEntry> Top(Dictionary<string, double> usage) =>
        [.. usage.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(TopCount).Select(pair => new UsageEntry(pair.Key, pair.Value))];

    private static DayStats ToDay(DateOnly date, DayData day) => new(date, day.Earned, day.Spent, day.Clicks, day.MediaSeconds);

    private static long Add(long total, long amount) => amount > long.MaxValue - total ? long.MaxValue : total + amount;
}
