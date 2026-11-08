using System.Collections.Concurrent;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Modules;
using ADHDWindows.Modules.Locks.Games;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Locks;

/// <summary>Блокировка программ: разовая покупка за монетки, до покупки программа свёрнута и заморожена.</summary>
public sealed class LocksModule : IModule
{
    /// <summary>Настройка для проверок: блокировать только файлы из этой папки.</summary>
    public const string OnlyUnderSetting = "onlyUnder";

    /// <summary>Настройка для проверок: папка Steam вместо найденной в реестре.</summary>
    public const string SteamPathSetting = "steamPath";

    /// <summary>Настройка для проверок: папка с файлами установок Epic Games.</summary>
    public const string EpicManifestsSetting = "epicManifests";

    private const int QueueLimit = 512;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LibraryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentQueue<Action<LockEngine>> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private IModuleContext? _context;
    private LockEngine? _engine;
    private GameLibrary? _games;
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
        var classifier = new ProgramClassifier(ProgramCatalog.Load(context.Data), OwnFiles(), onlyUnder, _games);
        _engine = new LockEngine(
            new WindowsProgramSystem(),
            classifier,
            new UnlockLedger(context.Saves),
            context.Economy,
            context.Changes,
            context.Events,
            context.Log,
            TimeProvider.System);

        context.Events.Subscribe<UnlockRequested>(request => Enqueue(engine => engine.Unlock(request.Key)));
        _watcher = new WindowWatcher(OnWindow, context.Log);
        _watcher.Start();
        _worker = new Thread(Work) { IsBackground = true, Name = "Locks" };
        _worker.Start();
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _stop.Cancel();
        _wake.Release();
        if (_worker?.Join(StopTimeout) == false)
        {
            _context?.Log.Warn("Поток блокировки не закрылся вовремя");
        }

        // Реестр изменений откатил бы заморозку и сам, но так программы оживают сразу и вместе с окнами.
        _engine?.ReleaseAll();
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
            Enqueue(engine => engine.OnWindow(window, activated));
        }
    }

    private void Enqueue(Action<LockEngine> command)
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
                Execute(engine =>
                {
                    if (_games!.Refresh())
                    {
                        engine.ForgetVerdicts();
                    }
                });
                nextLibrary = DateTime.UtcNow + LibraryInterval;
            }

            if (DateTime.UtcNow >= nextScan)
            {
                Execute(engine => engine.Scan(_stop.Token));
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

    private void Execute(Action<LockEngine> command)
    {
        try
        {
            command(_engine!);
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
