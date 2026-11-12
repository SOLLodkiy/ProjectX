using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Progress;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.Stats;
using ADHDWindows.Core.Upgrades;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Progress;

/// <summary>
/// Прогресс: опыт и уровни со званиями, достижения из data/progress, ежедневная серия, итог дня и цели недели.
/// Прогресс считается всегда, настройки отключают только уведомления и окно итога дня.
/// </summary>
public sealed class ProgressModule : IModule
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private IModuleContext? _context;
    private ProgressTracker? _tracker;
    private Thread? _worker;
    private ProcessStamp? _foreground;

    public string Id => "progress";

    public string Name => "Уровни, достижения и серии";

    internal ProgressTracker? Tracker => _tracker;

    public void Start(IModuleContext context)
    {
        _context = context;
        var catalog = ProgressCatalog.Load(context.Data, context.Log);
        var tracker = _tracker = new ProgressTracker(catalog, context.Economy, context.Events, context.Saves, context.Settings, TimeProvider.System, Random.Shared, context.Log);
        context.Log.Info($"Достижений: {catalog.Achievements.Count}, званий: {catalog.Ranks.Count}, целей недели: {catalog.WeeklyGoals.Count}");

        context.Events.Subscribe<ClickCounted>(tracker.OnClick);
        context.Events.Subscribe<CoinsEarned>(tracker.OnEarned);
        context.Events.Subscribe<CoinsSpent>(tracker.OnSpent);
        context.Events.Subscribe<MediaTimeCounted>(tracker.OnMedia);
        context.Events.Subscribe<ItemUnlocked>(tracker.OnUnlock);
        context.Events.Subscribe<UpgradePurchased>(tracker.OnUpgrade);
        context.Events.Subscribe<QuestCompleted>(tracker.OnQuest);
        context.Events.Subscribe<StatCounted>(tracker.OnCounter);
        context.Events.Subscribe<HardshipStarted>(tracker.OnHardshipStarted);
        context.Events.Subscribe<HardshipEnded>(tracker.OnHardshipEnded);
        context.Events.Subscribe<ProgressRequested>(_ => tracker.Publish());
        context.Events.Subscribe<ProgressSettingRequested>(request => tracker.SetSetting(request.Key, request.Value));

        tracker.Start();

        _worker = new Thread(Work) { IsBackground = true, Name = "Progress", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
        if (_worker?.Join(StopTimeout) == false)
        {
            _context?.Log.Warn("Поток прогресса не закрылся вовремя");
        }

        _tracker?.Save();
    }

    private void Work()
    {
        var nextSave = DateTime.UtcNow + SaveInterval;
        while (!_stop.Token.WaitHandle.WaitOne(TickInterval))
        {
            try
            {
                _tracker!.Tick();
                WatchForeground();
                if (DateTime.UtcNow >= nextSave)
                {
                    _tracker.Save();
                    nextSave = DateTime.UtcNow + SaveInterval;
                }
            }
            catch (Exception exception)
            {
                _context?.Log.Error("Ошибка прогресса", exception);
            }
        }
    }

    /// <summary>Новый процесс в активном окне засчитывается один раз: так считаются «открытые» программы.</summary>
    private void WatchForeground()
    {
        if (WindowControl.ForegroundProcessId() is not { } id || ProcessControl.Stamp(id) is not { } stamp || stamp == _foreground)
        {
            return;
        }

        _foreground = stamp;
        if (ProcessControl.ImagePath(stamp) is { } path)
        {
            _tracker!.OnProgram(Path.GetFileNameWithoutExtension(path));
        }
    }
}
