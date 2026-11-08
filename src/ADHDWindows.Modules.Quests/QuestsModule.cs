using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.Storage;
using ADHDWindows.Core.Upgrades;

namespace ADHDWindows.Modules.Quests;

/// <summary>
/// Задания трёх сложностей. Каждую сложность обновляет свой таймер, прогресс считается по событиям других механик,
/// награда зачисляется один раз. Шаблоны лежат в data/quests.
/// </summary>
public sealed class QuestsModule : IModule
{
    /// <summary>Настройка: интервал обновления в минутах, например <c>interval/easy</c>.</summary>
    public const string IntervalSettingPrefix = "interval/";

    /// <summary>Настройка: показывать уведомления о новых и выполненных заданиях.</summary>
    public const string NotifySetting = "notifications";

    /// <summary>Настройка: список отключённых типов заданий, например <c>["game", "spend"]</c>.</summary>
    public const string DisabledTypesSetting = "disabledTypes";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private IModuleContext? _context;
    private QuestBoard? _board;
    private Thread? _worker;

    public string Id => "quests";

    public string Name => "Задания";

    /// <summary>Доска, для проверок.</summary>
    internal QuestBoard? Board => _board;

    public void Start(IModuleContext context)
    {
        _context = context;
        var templates = QuestCatalog.Load(context.Data, context.Log);
        var board = _board = new QuestBoard(
            templates,
            ReadOptions(context.Settings),
            context.Economy,
            context.Events,
            context.Saves,
            context.Texts,
            TimeProvider.System,
            Random.Shared,
            context.Log);

        context.Events.Subscribe<ClickCounted>(board.OnClick);
        context.Events.Subscribe<MediaTimeCounted>(board.OnMedia);
        context.Events.Subscribe<CoinsSpent>(board.OnSpent);
        context.Events.Subscribe<UpgradePurchased>(board.OnUpgrade);
        context.Events.Subscribe<ItemUnlocked>(board.OnUnlock);
        context.Events.Subscribe<InstalledGamesChanged>(change => board.OnGames(change.Games));
        context.Events.Subscribe<CollectionChanged>(board.OnCollection);
        context.Events.Subscribe<UpgradeShopChanged>(board.OnShop);
        context.Events.Subscribe<QuestsRequested>(_ => board.Publish());
        if (context.DeveloperMode)
        {
            context.Events.Subscribe<QuestsRefreshRequested>(_ => board.RefreshAll());
        }

        context.Log.Info($"Шаблонов заданий: {templates.Count}");

        // Игры, покупки и магазин владельцы присылают из своих потоков, первые новые задания ждут их несколько секунд.
        context.Events.Publish(new InstalledGamesRequested());
        context.Events.Publish(new CollectionRequested());
        context.Events.Publish(new UpgradesRequested());
        board.Publish();

        _worker = new Thread(Work) { IsBackground = true, Name = "Quests", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
        if (_worker?.Join(StopTimeout) == false)
        {
            _context?.Log.Warn("Поток заданий не закрылся вовремя");
        }
    }

    internal static QuestOptions ReadOptions(IKeyValueStore settings)
    {
        var intervals = new Dictionary<QuestTier, TimeSpan>();
        foreach (var tier in Enum.GetValues<QuestTier>())
        {
            var minutes = settings.Get<double?>(IntervalSettingPrefix + tier.ToString().ToLowerInvariant());
            intervals[tier] = minutes is > 0 and < 7 * 24 * 60 ? TimeSpan.FromMinutes(minutes.Value) : QuestOptions.DefaultIntervals[tier];
        }

        var disabled = (settings.Get<string[]>(DisabledTypesSetting) ?? []).ToHashSet(StringComparer.Ordinal);
        return new QuestOptions(intervals, settings.Get(NotifySetting, true), disabled, Warmup);
    }

    private void Work()
    {
        while (!_stop.Token.WaitHandle.WaitOne(TickInterval))
        {
            try
            {
                _board!.Tick();
            }
            catch (Exception exception)
            {
                _context!.Log.Error("Ошибка таймера заданий", exception);
            }
        }
    }
}
