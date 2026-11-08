using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Media;

namespace ADHDWindows.Modules.Time;

/// <summary>Активное окно: процесс и его файл.</summary>
public sealed record ForegroundState(int? ProcessId, string? ImagePath)
{
    public static readonly ForegroundState None = new(null, null);
}

/// <summary>Системная сессия мультимедиа настольного клиента.</summary>
public sealed record DesktopMediaSession(string AppId, string? Title, string? Artist, bool Playing, double? Position, double? Duration, double Rate);

/// <summary>
/// Монетки за хронометраж: видео, музыка и игры. Все методы вызываются из одного рабочего потока.
/// <para>
/// Видео и игры считаются только в активном окне, и одновременно может считаться что-то одно.
/// Музыка играет фоном, но тоже только одна: два плеера сразу не приносят вдвое больше.
/// Для этого у каждого канала есть отметка «оплачено до», и пересекающиеся промежутки оплачиваются один раз.
/// </para>
/// </summary>
public sealed class TimeRewards
{
    /// <summary>Как часто накопленное зачисляется на баланс.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    /// <summary>Сколько нужно посмотреть, чтобы воспроизведение считалось начатым: заголовок страницы успевает смениться.</summary>
    public const double RegisterSeconds = 5;

    /// <summary>Самый длинный промежуток между осмотрами активного окна, который засчитывается игре.</summary>
    public static readonly TimeSpan MaxGameGap = TimeSpan.FromSeconds(5);

    /// <summary>Сколько после последнего засчитанного видео оно считается текущим занятием для вопроса «Ты тут?».</summary>
    private static readonly TimeSpan VideoActivityWindow = TimeSpan.FromSeconds(10);

    /// <summary>Плеер, о котором давно нет вестей, забывается.</summary>
    private static readonly TimeSpan StreamLifetime = TimeSpan.FromMinutes(30);

    private readonly TimeRules _rules;
    private readonly IEconomy _economy;
    private readonly IEventBus _events;
    private readonly ILog _log;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Stream> _streams = [];
    private readonly Dictionary<Activity, decimal> _pending = [];
    private readonly Dictionary<Activity, (string? Identity, int Count)> _repeats = [];
    private DateTimeOffset _foregroundPaidUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _musicPaidUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFlush;
    private DateTimeOffset _confirmedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastVideo = DateTimeOffset.MinValue;
    private ForegroundState _foreground = ForegroundState.None;
    private List<(string Folder, GameInstall Game)> _games = [];
    private (string? Path, GameInstall? Game) _gameCache;
    private string? _gameKey;
    private DateTimeOffset _gameSeenAt;
    private readonly bool _verbose;
    private readonly Dictionary<Activity, decimal> _totals = [];

    /// <param name="verbose">Писать в журнал итоги каждого зачисления: по ним сквозная проверка сверяет начисления.</param>
    public TimeRewards(TimeRules rules, IEconomy economy, IEventBus events, ILog log, TimeProvider clock, bool verbose = false)
    {
        _verbose = verbose;
        _rules = rules;
        _economy = economy;
        _events = events;
        _log = log;
        _clock = clock;
        _lastFlush = clock.GetUtcNow();
    }

    /// <summary>Начисление за видео и игры остановлено до ответа «Я тут».</summary>
    public bool WaitingForPresence { get; private set; }

    /// <summary>Накоплено и ещё не зачислено, для проверок.</summary>
    public decimal Pending(Activity activity) => _pending.GetValueOrDefault(activity);

    public void OnGames(IReadOnlyList<GameInstall> games)
    {
        _games = [.. games
            .Where(game => !string.IsNullOrWhiteSpace(game.Folder))
            .Select(game => (Folder: Normalize(game.Folder), Game: game))
            .OrderByDescending(item => item.Folder.Length)];
        _gameCache = default;
    }

    public void OnBrowserMedia(BrowserMediaReported report)
    {
        var prefix = $"b{report.PeerId}:";
        if (report.Disconnected)
        {
            foreach (var key in _streams.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _streams.Remove(key);
            }

            return;
        }

        var now = _clock.GetUtcNow();
        var browserInFront = report.BrowserProcessId is not { } browser || _foreground.ProcessId == browser;
        foreach (var tab in report.Tabs)
        {
            if (_rules.Classify(tab.Url) is not { } activity)
            {
                continue;
            }

            var host = Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) ? uri.Host : tab.Url;
            var visible = activity == Activity.Music || (tab.Focused && browserInFront);
            foreach (var media in tab.Media)
            {
                var duration = media.Duration is { } length ? Math.Round(length).ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
                var sample = new PlaybackSample(
                    media.Position,
                    media.Duration,
                    media.Playing && media.Audible,
                    media.Rate,
                    $"{host}|{media.Title}|{duration}");
                Feed($"{prefix}{tab.TabId}:{media.Id}", activity, sample, visible, now);
            }
        }
    }

    public void OnDesktopMedia(IReadOnlyList<DesktopMediaSession> sessions)
    {
        var now = _clock.GetUtcNow();
        foreach (var session in sessions)
        {
            if (_rules.FindApp(session.AppId) is null)
            {
                continue;
            }

            var sample = new PlaybackSample(
                session.Position,
                session.Duration,
                session.Playing,
                session.Rate,
                $"{session.AppId}|{session.Artist}|{session.Title}");
            Feed($"d:{session.AppId}", Activity.Music, sample, visible: true, now);
        }
    }

    /// <summary>Раз в секунду: игра в активном окне, вопрос «Ты тут?», зачисление и уборка.</summary>
    public void Tick(ForegroundState foreground, TimeSpan idle)
    {
        var now = _clock.GetUtcNow();
        _foreground = foreground;

        var game = FindGame(foreground.ImagePath);
        if (game is not null)
        {
            if (_gameKey == game.Key && now - _gameSeenAt is var gap && gap > TimeSpan.Zero && gap <= MaxGameGap && !WaitingForPresence)
            {
                var paid = Pay(ref _foregroundPaidUntil, now, gap.TotalSeconds);
                Add(Activity.Game, paid, 1);
            }

            _gameKey = game.Key;
            _gameSeenAt = now;
        }
        else
        {
            _gameKey = null;
        }

        Activity? current = game is not null ? Activity.Game : now - _lastVideo <= VideoActivityWindow ? Activity.Video : null;
        if (current is { } activity && !WaitingForPresence)
        {
            var away = idle < now - _confirmedAt ? idle : now - _confirmedAt;
            if (away >= _rules.PresenceLimit(activity))
            {
                WaitingForPresence = true;
                _log.Info($"Нет ввода {away.TotalMinutes:0} мин: начисление за {TimeRules.SourceOf(activity)} приостановлено до ответа");
                _events.Publish(new PresenceCheckChanged(true, TimeRules.SourceOf(activity)));
            }
        }

        if (now - _lastFlush >= FlushInterval)
        {
            Flush();
        }

        foreach (var key in _streams.Where(pair => now - pair.Value.SeenAt > StreamLifetime).Select(pair => pair.Key).ToList())
        {
            _streams.Remove(key);
        }
    }

    public void ConfirmPresence()
    {
        _confirmedAt = _clock.GetUtcNow();
        if (WaitingForPresence)
        {
            WaitingForPresence = false;
            _log.Info("Игрок ответил «Я тут»");
            _events.Publish(new PresenceCheckChanged(false, string.Empty));
        }
    }

    /// <summary>Зачисляет накопленное. Дробные монетки копит кошелёк.</summary>
    public void Flush()
    {
        _lastFlush = _clock.GetUtcNow();
        foreach (var (activity, amount) in _pending.ToList())
        {
            if (amount > 0)
            {
                _economy.Earn(TimeRules.SourceOf(activity), amount);
                _totals[activity] = _totals.GetValueOrDefault(activity) + amount;
            }

            _pending[activity] = 0;
        }

        if (_verbose)
        {
            var totals = string.Join(' ', Enum.GetValues<Activity>().Select(activity =>
                $"{TimeRules.SourceOf(activity)}={_totals.GetValueOrDefault(activity).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}"));
            _log.Info($"Хронометраж всего: {totals}");
        }
    }

    private void Feed(string key, Activity activity, PlaybackSample sample, bool visible, DateTimeOffset now)
    {
        if (!_streams.TryGetValue(key, out var stream))
        {
            _streams[key] = stream = new Stream(activity, new PlaybackMeter(_rules.CompletionShare, _rules.MinCompletionSeconds));
        }

        stream.SeenAt = now;
        var step = stream.Meter.Feed(sample, now);
        if (step.NewPlay)
        {
            stream.Earned = 0;
            stream.Factor = 1;
            stream.Registered = false;
        }

        if (!stream.Registered && stream.Meter.Covered >= RegisterSeconds)
        {
            stream.Registered = true;
            stream.Factor = Register(activity, sample.Identity);
        }

        if (step.Credit > 0 && visible)
        {
            if (activity == Activity.Video)
            {
                _lastVideo = now;
            }

            if (activity == Activity.Music || !WaitingForPresence)
            {
                var paid = activity == Activity.Music
                    ? Pay(ref _musicPaidUntil, now, step.Credit)
                    : Pay(ref _foregroundPaidUntil, now, step.Credit);
                var earned = Add(activity, paid, stream.Factor);
                stream.Earned += earned;

                // Последние секунды досмотренного ролика тоже получают бонус.
                if (stream.Meter.Completed && !step.Completed)
                {
                    _pending[activity] = Pending(activity) + (earned * _rules.CompletionBonus);
                }
            }
        }

        if (step.Completed && stream.Earned > 0)
        {
            var bonus = stream.Earned * _rules.CompletionBonus;
            _pending[activity] = Pending(activity) + bonus;
            _log.Info($"Досмотрено ({TimeRules.SourceOf(activity)}): бонус {bonus:0.##}");
        }
    }

    /// <summary>Номер повтора подряд и его множитель.</summary>
    private decimal Register(Activity activity, string identity)
    {
        var (last, count) = _repeats.GetValueOrDefault(activity);
        count = last == identity ? count + 1 : 1;
        _repeats[activity] = (identity, count);
        var factor = _rules.RepeatFactor(count);
        if (factor < 1)
        {
            _log.Info($"Повтор подряд №{count} ({TimeRules.SourceOf(activity)}): множитель {factor}");
        }

        return factor;
    }

    private decimal Add(Activity activity, double seconds, decimal factor)
    {
        if (seconds <= 0)
        {
            return 0;
        }

        var amount = (decimal)seconds / 60m * _rules.RateOf(activity) * factor;
        _pending[activity] = Pending(activity) + amount;
        return amount;
    }

    /// <summary>Оплачивает промежуток [now − seconds, now] за вычетом уже оплаченного в этом канале.</summary>
    private static double Pay(ref DateTimeOffset paidUntil, DateTimeOffset now, double seconds)
    {
        var start = now - TimeSpan.FromSeconds(seconds);
        var from = start > paidUntil ? start : paidUntil;
        if (now > paidUntil)
        {
            paidUntil = now;
        }

        return Math.Max(0, (now - from).TotalSeconds);
    }

    private GameInstall? FindGame(string? path)
    {
        if (path is null || _games.Count == 0)
        {
            return null;
        }

        if (_gameCache.Path == path)
        {
            return _gameCache.Game;
        }

        var normalized = Normalize(path);
        var game = _games.FirstOrDefault(item => normalized.StartsWith(item.Folder, StringComparison.OrdinalIgnoreCase)).Game;
        _gameCache = (path, game);
        return game;
    }

    private static string Normalize(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Путь остаётся как есть.
        }

        return path.TrimEnd('\\', '/') + "\\";
    }

    private sealed class Stream(Activity activity, PlaybackMeter meter)
    {
        public Activity Activity => activity;
        public PlaybackMeter Meter => meter;
        public DateTimeOffset SeenAt { get; set; }

        /// <summary>Заработано на текущем воспроизведении: от этого считается бонус за досмотр.</summary>
        public decimal Earned { get; set; }

        public decimal Factor { get; set; } = 1;
        public bool Registered { get; set; }
    }
}
