using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Modules;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships;

/// <summary>
/// Планировщик усложнений. Сами усложнения — отдельные модули на основе <see cref="HardshipModule"/>:
/// они сообщают о себе через шину, и новое усложнение подключается без правки планировщика и ядра.
/// </summary>
public sealed class HardshipsModule : IModule
{
    /// <summary>Настройка: откладывать усложнения, пока занят микрофон.</summary>
    public const string WatchCallsSetting = "quiet/calls";

    /// <summary>Настройка режима разработчика: не проверять ограничения безопасности.</summary>
    public const string IgnoreSafetySetting = "test/ignoreSafety";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private IModuleContext? _context;
    private HardshipScheduler? _scheduler;
    private Thread? _worker;
    private bool _watchCalls = true;

    public string Id => "hardships";

    public string Name => "Усложнения";

    internal HardshipScheduler? Scheduler => _scheduler;

    /// <summary>Проверка ограничений безопасности. Для тестов подменяется.</summary>
    internal Func<string?> Safety { get; set; } = () => null;

    public void Start(IModuleContext context)
    {
        _context = context;
        _watchCalls = context.Settings.Get(WatchCallsSetting, true);
        Safety = () => QuietDetector.Check(_watchCalls);
        if (context.DeveloperMode && context.Settings.Get(IgnoreSafetySetting, false))
        {
            // Только для автоматических проверок: окно с видео во весь экран на соседнем мониторе не должно им мешать.
            context.Log.Warn($"Режим разработчика: ограничения безопасности усложнений выключены настройкой {IgnoreSafetySetting}");
            Safety = () => null;
        }
        var rules = HardshipRules.Load(context.Data, context.Log);
        var scheduler = _scheduler = new HardshipScheduler(
            rules,
            context.Economy,
            context.Events,
            context.Saves,
            context.Settings,
            context.Texts,
            TimeProvider.System,
            Random.Shared,
            context.Log);

        context.Events.Subscribe<HardshipOffered>(offered => scheduler.Offer(offered.Hardship));
        context.Events.Subscribe<HardshipFailed>(scheduler.OnFailed);
        context.Events.Subscribe<HardshipStatusRequested>(_ => scheduler.PublishStatus());
        context.Events.Subscribe<HardshipModeRequested>(request => scheduler.SetMode(request.Mode));
        context.Events.Subscribe<HardshipToggleRequested>(request => scheduler.SetEnabled(request.Id, request.Enabled));
        context.Events.Subscribe<HardshipQuietRequested>(request => scheduler.SetQuiet(request.Duration));
        context.Events.Subscribe<HardshipCancelRequested>(_ => scheduler.Cancel());
        if (context.DeveloperMode)
        {
            context.Events.Subscribe<HardshipTestRequested>(request => scheduler.Test(request.Id, CheckSafety()));
        }

        // Модули усложнений, запущенные раньше планировщика, отвечают на просьбу; запущенные позже сообщат сами.
        context.Events.Publish(new HardshipsRequested());
        context.Events.Subscribe<AppStarted>(_ => context.Log.Info(
            $"Режим усложнений: {scheduler.Mode}, усложнений: {scheduler.Offered.Count}, следующее в {scheduler.Next.ToLocalTime():HH:mm}"));
        scheduler.PublishStatus();

        _worker = new Thread(Work) { IsBackground = true, Name = "Hardships", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
        if (_worker?.Join(StopTimeout) == false)
        {
            _context?.Log.Warn("Поток усложнений не закрылся вовремя");
        }

        _scheduler?.Dispose();
    }

    private string? CheckSafety()
    {
        try
        {
            return Safety();
        }
        catch (Exception exception)
        {
            // Не удалось проверить — значит, мешать нельзя.
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
            }
            catch (Exception exception)
            {
                _context!.Log.Error("Ошибка таймера усложнений", exception);
            }
        }
    }
}
