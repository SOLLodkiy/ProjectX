using System.Collections.Concurrent;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Modules;
using ADHDWindows.Modules.Locks.Games;
using ADHDWindows.Modules.Locks.Sites;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Locks;

/// <summary>
/// Блокировка программ, игр и сайтов: разовая покупка за монетки. До покупки программа свёрнута и заморожена,
/// а сайт закрыт экраном покупки расширения браузера.
/// </summary>
public sealed class LocksModule : IModule
{
    /// <summary>Настройка для проверок: блокировать только файлы из этой папки.</summary>
    public const string OnlyUnderSetting = "onlyUnder";

    /// <summary>Настройка для проверок: папка Steam вместо найденной в реестре.</summary>
    public const string SteamPathSetting = "steamPath";

    /// <summary>Настройка для проверок: папка с файлами установок Epic Games.</summary>
    public const string EpicManifestsSetting = "epicManifests";

    /// <summary>Настройка для проверок: следить только за браузерами с этим текстом в командной строке.</summary>
    /// <remarks>В режиме разработчика без неё браузеры без расширения не сворачиваются.</remarks>
    public const string BrowserProfileSetting = "browserProfile";

    private const int QueueLimit = 512;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LibraryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BrowserCheckPause = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private IModuleContext? _context;
    private LockEngine? _engine;
    private GameLibrary? _games;
    private UnlockLedger? _ledger;
    private ProgramCatalog? _programCatalog;
    private SiteCatalog? _siteCatalog;
    private SiteLocks? _sites;
    private BrowserWatch? _browsers;
    private BrowserBridge? _bridge;
    private DateTime _nextBrowserCheck;
    private bool _gamesPublished;
    private WindowWatcher? _watcher;
    private Thread? _worker;

    public string Id => "locks";

    public string Name => "Блокировка программ";

    public void Start(IModuleContext context)
    {
        _context = context;
        var onlyUnder = context.Settings.Get<string>(OnlyUnderSetting);
        if (string.IsNullOrWhiteSpace(onlyUnder))
        {
            onlyUnder = null;
        }

        // Проверка не должна замораживать настоящие программы человека из-за забытой настройки.
        if (context.DeveloperMode && onlyUnder is null)
        {
            context.Log.Warn($"Режим разработчика: блокировка выключена, пока не задана настройка {OnlyUnderSetting}");
            return;
        }

        if (onlyUnder is not null)
        {
            context.Log.Info($"Блокировка ограничена папкой: {onlyUnder}");
        }

        var gameCatalog = GameCatalog.Load(context.Data);
        _games = new GameLibrary(GameSources(context), gameCatalog, context.Log, TimeProvider.System);
        _programCatalog = ProgramCatalog.Load(context.Data);
        var classifier = new ProgramClassifier(_programCatalog, OwnFiles(), onlyUnder, _games);
        var siteCatalog = SiteCatalog.Load(context.Data);
        _siteCatalog = siteCatalog;
        var system = new WindowsProgramSystem();

        // Одна книга покупок на программы и сайты: обе части пишут в одно сохранение из одного потока.
        var ledger = new UnlockLedger(context.Saves);
        _ledger = ledger;
        _engine = new LockEngine(
            system,
            classifier,
            ledger,
            context.Economy,
            context.Changes,
            context.Events,
            context.Log,
            TimeProvider.System);

        _sites = new SiteLocks(new SiteClassifier(siteCatalog), ledger, context.Economy, context.Events, context.Log, TimeProvider.System);
        var profile = Setting(context, BrowserProfileSetting);
        if (!context.DeveloperMode || profile is not null)
        {
            _browsers = new BrowserWatch(siteCatalog, system, new WindowsBrowserSystem(), context.Events, context.Log, TimeProvider.System, profile);
        }
        else
        {
            context.Log.Warn($"Режим разработчика: браузеры без расширения не сворачиваются, пока не задана настройка {BrowserProfileSetting}");
        }

        NativeHost.Register(NativeHost.DefaultHostPath, context.Log);
        _bridge = new BrowserBridge(
            ScopedName.BrowserBridge(),
            (peer, message) => Enqueue(() => _sites.OnMessage(peer, message)),
            context.Log);
        _bridge.Start();

        context.Events.Subscribe<InstalledGamesRequested>(_ => Enqueue(PublishGames));
        context.Events.Subscribe<CollectionRequested>(_ => Enqueue(PublishCollection));
        context.Events.Subscribe<ItemUnlocked>(_ => Enqueue(PublishCollection));
        context.Events.Subscribe<UnlockRequested>(request => Enqueue(() => _engine.Unlock(request.Key)));
        context.Events.Subscribe<ExtensionPageRequested>(request => Enqueue(() => _browsers?.OpenExtensionsPage(request.BrowserId)));
        _watcher = new WindowWatcher(OnWindow, context.Log);
        _watcher.Start();
        _worker = new Thread(Work) { IsBackground = true, Name = "Locks" };
        _worker.Start();
    }

    public void Stop()
    {
        _watcher?.Dispose();

        // Расширения узнают, что программы нет, и открывают закрытые вкладки.
        _bridge?.Dispose();
        _stop.Cancel();
        _wake.Release();
        if (_worker?.Join(StopTimeout) == false)
        {
            _context?.Log.Warn("Поток блокировки не закрылся вовремя");
        }

        // Реестр изменений откатил бы заморозку и сам, но так программы оживают сразу и вместе с окнами.
        _engine?.ReleaseAll();
        _browsers?.ReleaseAll();
    }

    private static List<IGameSource> GameSources(IModuleContext context)
    {
        return
        [
            new SteamSource(Setting(context, SteamPathSetting), context.Log),
            new EpicSource(Setting(context, EpicManifestsSetting), context.Log),
            RegistrySource.Gog(context.Log),
            RegistrySource.Ubisoft(context.Log),
        ];
    }

    private static string? Setting(IModuleContext context, string name) =>
        context.Settings.Get<string>(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static IEnumerable<string> OwnFiles()
    {
        if (Environment.ProcessPath is { } self)
        {
            yield return self;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "watchdog", "ADHDWindows.Watchdog.exe");
    }

    private void OnWindow(IntPtr window, bool activated)
    {
        // Показов окон бывает очень много. Потерянное событие подберёт следующий осмотр.
        if (_queue.Count < QueueLimit)
        {
            Enqueue(() =>
            {
                _engine!.OnWindow(window, activated);

                // Развёрнутое окно браузера без расширения сворачивается сразу, а не при следующем осмотре.
                if (activated && DateTime.UtcNow >= _nextBrowserCheck)
                {
                    CheckBrowsers();
                }
            });
        }
    }

    /// <summary>Список игр нужен и другим модулям, например хронометражу.</summary>
    private void PublishGames()
    {
        _gamesPublished = true;
        _context!.Events.Publish(new InstalledGamesChanged([.. _games!.Games.Select(game => new GameInstall(game.Key, game.Name, game.Folder))]));
        PublishCollection();
    }

    /// <summary>Коллекция купленного для статистики: меняется после покупки и при новых установках игр.</summary>
    private void PublishCollection() =>
        _context!.Events.Publish(CollectionReport.Build(
            _ledger!.Records,
            _programCatalog!,
            _siteCatalog!,
            _games!.Games.Select(game => game.Key)));

    private void CheckBrowsers()
    {
        _browsers?.Check(_sites!.Sessions);
        _nextBrowserCheck = DateTime.UtcNow + BrowserCheckPause;
    }

    private void Enqueue(Action command)
    {
        _queue.Enqueue(command);
        _wake.Release();
    }

    private void Work()
    {
        var nextScan = DateTime.UtcNow;
        var nextLibrary = DateTime.UtcNow;
        while (!_stop.IsCancellationRequested)
        {
            // Новая установка получает цену сразу. Уже открытые процессы пересматриваются.
            if (DateTime.UtcNow >= nextLibrary)
            {
                Execute(() =>
                {
                    if (_games!.Refresh() || !_gamesPublished)
                    {
                        _engine!.ForgetVerdicts();
                        PublishGames();
                    }
                });
                nextLibrary = DateTime.UtcNow + LibraryInterval;
            }

            if (DateTime.UtcNow >= nextScan)
            {
                Execute(() => _engine!.Scan(_stop.Token));
                Execute(CheckBrowsers);
                nextScan = DateTime.UtcNow + ScanInterval;
            }

            while (!_stop.IsCancellationRequested && _queue.TryDequeue(out var command))
            {
                Execute(command);
            }

            var delay = nextScan - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                _wake.Wait(delay);
            }
        }
    }

    private void Execute(Action command)
    {
        try
        {
            command();
        }
        catch (OperationCanceledException)
        {
            // Программа закрывается.
        }
        catch (Exception exception)
        {
            _context?.Log.Error("Ошибка блокировки", exception);
        }
    }
}
