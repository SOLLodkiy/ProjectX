using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.RandomEvents;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Events;

/// <summary>
/// Планировщик случайных событий и бустеров. Сами события — отдельные модули на <see cref="RandomEventModule"/>:
/// они сообщают о себе через шину, поэтому новое событие подключается без правки планировщика и ядра.
/// </summary>
public sealed class EventsModule : IModule
{
    /// <summary>Настройка: не начинать события, пока занят микрофон.</summary>
    public const string WatchCallsSetting = "quiet/calls";

    /// <summary>Настройка режима разработчика: не проверять ограничения безопасности.</summary>
    public const string IgnoreSafetySetting = "test/ignoreSafety";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private IModuleContext? _context;
    private EventScheduler? _scheduler;
    private BoosterBook? _boosters;
    private Thread? _worker;

    public string Id => "events";

    public string Name => "Случайные события";

    internal EventScheduler? Scheduler => _scheduler;

    internal BoosterBook? Boosters => _boosters;

    /// <summary>Проверка ограничений безопасности. Для тестов подменяется.</summary>
    internal Func<string?> Safety { get; set; } = () => null;

    public void Start(IModuleContext context)
    {
        _context = context;
        var watchCalls = context.Settings.Get(WatchCallsSetting, true);
        Safety = () => QuietDetector.Check(watchCalls);
        if (context.DeveloperMode && context.Settings.Get(IgnoreSafetySetting, false))
        {
            context.Log.Warn($"Режим разработчика: ограничения безопасности событий выключены настройкой {IgnoreSafetySetting}");
            Safety = () => null;
        }

        var boosters = _boosters = new BoosterBook(context.Economy, context.Events, context.Saves, context.Texts, TimeProvider.System, context.Log);
        var scheduler = _scheduler = new EventScheduler(
            EventRules.Load(context.Data, context.Log),
            context.Events,
            context.Saves,
            context.Settings,
            context.Texts,
            TimeProvider.System,
            Random.Shared,
            context.Log);

        context.Events.Subscribe<RandomEventOffered>(offered => scheduler.Offer(offered.Event));
        context.Events.Subscribe<RandomEventFinished>(scheduler.OnFinished);
        context.Events.Subscribe<RandomEventFailed>(scheduler.OnFailed);
        context.Events.Subscribe<RandomEventStatusRequested>(_ => scheduler.PublishStatus());
        context.Events.Subscribe<RandomEventFrequencyRequested>(request => scheduler.SetFrequency(request.Frequency));
        context.Events.Subscribe<RandomEventToggleRequested>(request => scheduler.SetEnabled(request.Id, request.Enabled));
        context.Events.Subscribe<RandomEventQueueRequested>(scheduler.Enqueue);
        context.Events.Subscribe<RandomEventOpenRequested>(request => scheduler.Open(request.Id));
        context.Events.Subscribe<HardshipStatus>(scheduler.OnHardshipStatus);
        context.Events.Subscribe<HardshipWarned>(scheduler.OnHardshipWarned);
        context.Events.Subscribe<HardshipStarted>(_ => scheduler.OnHardshipStarted());
        context.Events.Subscribe<HardshipEnded>(_ => scheduler.OnHardshipEnded());
        context.Events.Subscribe<BoosterGrantRequested>(boosters.Grant);
        context.Events.Subscribe<BoostersRequested>(_ => boosters.Publish());
        if (context.DeveloperMode)
        {
            context.Events.Subscribe<RandomEventTestRequested>(request => scheduler.Test(request.Id, request.Variant, CheckSafety()));
        }

        // Модули событий, запущенные раньше планировщика, отвечают на просьбу; запущенные позже сообщат сами.
        context.Events.Publish(new RandomEventsRequested());
        context.Events.Publish(new HardshipStatusRequested());
        context.Events.Subscribe<AppStarted>(_ => context.Log.Info(
            $"Частота событий: {scheduler.Frequency}, событий: {scheduler.Offered.Count}, следующее в {scheduler.Next.ToLocalTime():HH:mm}"));
        scheduler.PublishStatus();
        boosters.Publish();

        _worker = new Thread(Work) { IsBackground = true, Name = "Events", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
        if (_worker?.Join(StopTimeout) == false)
        {
            _context?.Log.Warn("Поток событий не закрылся вовремя");
        }

        _scheduler?.Dispose();
        _boosters?.Dispose();
    }

    private string? CheckSafety()
    {
        try
        {
            return Safety();
        }
        catch (Exception exception)
        {
            _context?.Log.Error("Не удалось проверить ограничения безопасности", exception);
            return "unknown";
        }
    }

    private void Work()
    {
        while (!_stop.Token.WaitHandle.WaitOne(TickInterval))
        {
            try
            {
                _scheduler!.Tick(CheckSafety());
                _boosters!.Tick();
            }
            catch (Exception exception)
            {
                _context!.Log.Error("Ошибка таймера событий", exception);
            }
        }
    }
}
