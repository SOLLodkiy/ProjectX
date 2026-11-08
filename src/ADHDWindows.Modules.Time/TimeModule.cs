using System.Collections.Concurrent;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Upgrades;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Time;

/// <summary>
/// Монетки за хронометраж: видео и музыку на сайтах видит расширение браузера, настольные клиенты — системные сессии
/// мультимедиа, а игры — список установленных игр от модуля блокировки. Модуль ничего в системе не меняет.
/// </summary>
public sealed class TimeModule : IModule
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DesktopInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private const int QueueLimit = 256;

    /// <summary>
    /// Настройка для проверок, только в режиме разработчика: дополнительные сайты, например
    /// «video.test=video, music.test=music». С ней же в журнал пишутся итоги каждого зачисления.
    /// </summary>
    public const string TestSitesSetting = "testSites";

    /// <summary>Эффект улучшения: бонус за досмотр больше на <c>percent</c> процентов за уровень.</summary>
    public const string CompletionBonusEffect = "time.completionBonus";

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly InputActivity _input = new();
    private IModuleContext? _context;
    private TimeRewards? _rewards;
    private DesktopMedia? _desktop;
    private Thread? _worker;
    private (ProcessStamp Stamp, string? Path)? _foregroundCache;

    public string Id => "time";

    public string Name => "Монетки за хронометраж";

    public void Start(IModuleContext context)
    {
        _context = context;
        var rules = TimeRules.Load(context.Data);
        var testSites = context.DeveloperMode ? context.Settings.Get<string>(TestSitesSetting) : null;
        if (!string.IsNullOrWhiteSpace(testSites))
        {
            rules = rules.WithSites(TimeRules.ParseSites(testSites));
            context.Log.Info($"Режим разработчика: сайты для проверки хронометража: {testSites}");
        }

        _rewards = new TimeRewards(rules, context.Economy, context.Events, context.Log, TimeProvider.System, verbose: !string.IsNullOrWhiteSpace(testSites), texts: context.Texts);
        _desktop = new DesktopMedia(appId => rules.FindApp(appId) is not null, context.Log);

        context.Events.Subscribe<BrowserMediaReported>(report => Enqueue(() => _rewards.OnBrowserMedia(report)));
        context.Events.Subscribe<InstalledGamesChanged>(change => Enqueue(() => _rewards.OnGames(change.Games)));
        context.Events.Subscribe<PresenceConfirmed>(_ => Enqueue(_rewards.ConfirmPresence));
        context.Events.Subscribe<UpgradesChanged>(change =>
        {
            var extra = UpgradeEffects.Total(change.Owned, CompletionBonusEffect, "percent") / 100;
            Enqueue(() => _rewards.ExtraCompletionBonus = extra);
        });

        _worker = new Thread(Work) { IsBackground = true, Name = "Time" };
        _worker.Start();

        // Модуль блокировки мог запуститься раньше и уже разослать список игр.
        context.Events.Publish(new InstalledGamesRequested());
        context.Events.Publish(new UpgradesRequested());
    }

    public void Stop()
    {
        _stop.Cancel();
        _wake.Release();
        if (_worker?.Join(StopTimeout) == false)
        {
            _context?.Log.Warn("Поток хронометража не закрылся вовремя");
        }

        // Накопленное за последние секунды не пропадает.
        try
        {
            _rewards?.Flush();
            if (_rewards?.WaitingForPresence == true)
            {
                _context?.Events.Publish(new PresenceCheckChanged(false, string.Empty));
            }
        }
        catch (Exception exception)
        {
            _context?.Log.Error("Не удалось зачислить хронометраж при выходе", exception);
        }
    }

    private void Enqueue(Action command)
    {
        // Отчёты браузера приходят каждые пару секунд. Если поток завис, старые отчёты не нужны.
        if (_queue.Count < QueueLimit)
        {
            _queue.Enqueue(command);
            _wake.Release();
        }
    }

    private void Work()
    {
        var nextTick = DateTime.UtcNow;
        var nextDesktop = DateTime.UtcNow;
        while (!_stop.IsCancellationRequested)
        {
            while (!_stop.IsCancellationRequested && _queue.TryDequeue(out var command))
            {
                Execute(command);
            }

            if (DateTime.UtcNow >= nextDesktop)
            {
                Execute(() => _rewards!.OnDesktopMedia(_desktop!.Read()));
                nextDesktop = DateTime.UtcNow + DesktopInterval;
            }

            if (DateTime.UtcNow >= nextTick)
            {
                Execute(() => _rewards!.Tick(Foreground(), _input.Idle()));
                nextTick = DateTime.UtcNow + TickInterval;
            }

            var delay = nextTick - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                _wake.Wait(delay);
            }
        }
    }

    private ForegroundState Foreground()
    {
        if (WindowControl.ForegroundProcessId() is not { } id || ProcessControl.Stamp(id) is not { } stamp)
        {
            return ForegroundState.None;
        }

        // Путь к файлу спрашивается у системы только при смене процесса.
        if (_foregroundCache is not { } cached || cached.Stamp != stamp)
        {
            _foregroundCache = cached = (stamp, ProcessControl.ImagePath(stamp));
        }

        return new ForegroundState(id, cached.Path);
    }

    private void Execute(Action command)
    {
        try
        {
            command();
        }
        catch (Exception exception)
        {
            _context?.Log.Error("Ошибка хронометража", exception);
        }
    }
}
