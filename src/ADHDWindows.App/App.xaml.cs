using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ADHDWindows.App.Cosmetics;
using ADHDWindows.App.Design;
using ADHDWindows.App.Events;
using ADHDWindows.App.Hardships;
using ADHDWindows.App.Hud;
using ADHDWindows.App.Locks;
using ADHDWindows.App.Progress;
using ADHDWindows.App.Quests;
using ADHDWindows.App.Shop;
using ADHDWindows.App.Stats;
using ADHDWindows.App.Time;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.Safety;
using ADHDWindows.Platform;

namespace ADHDWindows.App;

public partial class App : Application
{
    private const string DeveloperGrantSource = "developer";
    private const int DeveloperGrant = 1000;
    private const string ManualProbeToken = "tray";

    /// <summary>Настройка: сочетание клавиш окна «Логи».</summary>
    private const string StatsHotkeySetting = "stats/hotkey";

    private static readonly Hotkey DefaultStatsHotkey = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'L');

    private FileLog? _log;
    private SingleInstanceGuard? _instanceGuard;
    private GameHost? _host;
    private WatchdogProcess? _watchdog;
    private NamedSignal? _emergencyStop;
    private NamedSignal? _hotkeyBusy;
    private Hotkey _hotkey = Hotkey.Default;
    private MouseHook? _mouseHook;
    private TrayIcon? _tray;
    private HudWindow? _hud;
    private LockScreen? _lockScreen;
    private ExtensionScreen? _extensionScreen;
    private PresenceScreen? _presenceScreen;
    private ShopScreen? _shopScreen;
    private StatsScreen? _statsScreen;
    private QuestScreen? _questScreen;
    private ProgressScreen? _progressScreen;
    private HardshipScreen? _hardshipScreen;
    private EventScreen? _eventScreen;
    private CosmeticsScreen? _cosmeticsScreen;
    private GlobalHotkey? _statsHotkey;
    private ShowcaseWindow? _showcase;
    private bool _designReady;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = AppOptions.Parse(e.Args);
        var paths = AppPaths.ForCurrentUser(options.UserDir);
        var log = new FileLog(paths.LogsDir);
        _log = log;
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;

        _instanceGuard = new SingleInstanceGuard(SingleInstanceGuard.NameFor(options.UserDir));
        if (!_instanceGuard.TryAcquire())
        {
            log.Info("Программа уже запущена, вторая копия закрывается");
            Shutdown();
            return;
        }

        log.Info($"Запуск, режим разработчика: {options.DeveloperMode}");
        _host = new GameHost(paths, options, log);
        _lockScreen = new LockScreen(_host.Events, _host.Economy, _host.Texts, () => _designReady, Dispatcher);
        _extensionScreen = new ExtensionScreen(_host.Events, _host.Texts, () => _designReady, Dispatcher);
        _presenceScreen = new PresenceScreen(_host.Events, _host.Texts, () => _designReady, Dispatcher);
        _shopScreen = new ShopScreen(_host.Events, _host.Economy, _host.Texts, () => _designReady, Dispatcher);
        _statsScreen = new StatsScreen(_host.Events, _host.Texts, () => _designReady, Dispatcher, _host.ResetProgress, log);
        _questScreen = new QuestScreen(_host.Events, _host.Texts, () => _designReady, Dispatcher);
        _progressScreen = new ProgressScreen(_host.Events, _host.Texts, () => _designReady, Dispatcher);
        _hardshipScreen = new HardshipScreen(_host.Events, _host.Texts, () => _designReady, Dispatcher, options.DeveloperMode);
        _eventScreen = new EventScreen(_host.Events, _host.Economy, _host.Texts, () => _designReady, Dispatcher, options.DeveloperMode);
        _cosmeticsScreen = new CosmeticsScreen(_host.Events, _host.Economy, _host.Texts, () => _designReady, Dispatcher);
        _host.Start(() => StartWatchdog(paths, log));
        _statsScreen.Request();
        _questScreen.Request();
        _progressScreen.Request();
        _hardshipScreen.Request();
        _eventScreen.Request();
        _cosmeticsScreen.Request();
        _mouseHook = new MouseHook(_host.Events.Publish, log);
        var mouseHook = _mouseHook;
        _host.Events.Subscribe<MouseButtonsSwapped>(swap => mouseHook.Unswap = swap.Swapped);
        _mouseHook.Start();

        _designReady = InstallDesignSystem(paths, log);
        if (_designReady)
        {
            _cosmeticsScreen.ApplyLook();
        }

        var hudSettings = _designReady ? new HudSettings(_host.Settings) : null;
        _tray = new TrayIcon(
            _host.Texts,
            Path.Combine(paths.AssetsDir, "app.ico"),
            options.DeveloperMode,
            new TrayActions(
                hudSettings,
                _designReady ? OpenShowcase : null,
                () => _host.Economy.Earn(DeveloperGrantSource, DeveloperGrant),
                () => _host.Events.Publish(new RecoveryProbeRequested(ManualProbeToken)),
                () => _host.Events.Publish(new QuestsRefreshRequested()),
                () => _shopScreen.Open(),
                () => _questScreen.Open(),
                () => _progressScreen.Open(),
                () => _statsScreen.Open(),
                () => _lockScreen.Open(),
                () => Shutdown(),
                _hotkey.ToString(),
                _hardshipScreen.Fill,
                _eventScreen.Fill,
                () => _cosmeticsScreen.Open()));

        if (hudSettings is not null)
        {
            _hud = new HudWindow(_host.Economy, _host.Events, _host.Texts, hudSettings, log);
            _hud.ConnectShop(_shopScreen.Open, () => _shopScreen.Available);
            _hud.ConnectLogs(_statsScreen.Open, () => _statsScreen.Available);
            _hud.ConnectQuests(_questScreen.Open, () => _questScreen.Available);
            _hud.ConnectProgress(_progressScreen.Open, () => _progressScreen.Available);
            _hud.Start();
        }

        StartStatsHotkey(log);
        _progressScreen.ShowPending();

        // Модуль мог заблокировать уже открытые программы раньше, чем загрузилась дизайн-система.
        _lockScreen.Open();
        _extensionScreen.Open();

        if (_designReady && options.OpenShowcase)
        {
            OpenShowcase();
        }

        if (options.RecoveryProbe is { } token)
        {
            _host.Events.Publish(new RecoveryProbeRequested(token));
        }

        if (options.Hardship is { } hardship)
        {
            // Модули усложнений отвечают из потока планировщика, поэтому запуск не задерживает интерфейс.
            Task.Run(() => _host.Events.Publish(new HardshipTestRequested(hardship == "random" ? null : hardship)));
        }

        if (options.Event is { } randomEvent)
        {
            var parts = randomEvent.Split(':', 2);
            Task.Run(() => _host.Events.Publish(new ADHDWindows.Core.RandomEvents.RandomEventTestRequested(
                parts[0] == "random" ? null : parts[0],
                parts.Length > 1 ? parts[1] : null)));
        }
    }

    /// <summary>Горячая клавиша окна «Логи». Занятое сочетание не мешает работе: окно открывается из HUD и трея.</summary>
    private void StartStatsHotkey(FileLog log)
    {
        var text = _host!.Settings.Get<string>(StatsHotkeySetting);
        if (!Hotkey.TryParse(text, out var hotkey))
        {
            if (text is not null)
            {
                log.Warn($"Неверное сочетание клавиш в настройке {StatsHotkeySetting}: {text}");
            }

            hotkey = DefaultStatsHotkey;
        }

        if (hotkey == _hotkey)
        {
            log.Warn($"Клавиша логов совпадает с аварийной остановкой и не подключена: {hotkey}");
            return;
        }

        _statsHotkey = new GlobalHotkey(hotkey, () => Dispatcher.BeginInvoke(() => _statsScreen?.Open()), log);
        if (!_statsHotkey.Start())
        {
            log.Warn($"Клавиша логов занята: {hotkey}");
            ShowSafetyWarning("stats.hotkeyBusy", hotkey);
        }
    }

    private void StartWatchdog(AppPaths paths, FileLog log)
    {
        _hotkey = Hotkey.FromSettings(_host!.Settings, log);
        var session = _host.Recovery.Session;
        _emergencyStop = new NamedSignal(ScopedName.EmergencyStop(session), OnEmergencyStop);
        _hotkeyBusy = new NamedSignal(
            ScopedName.HotkeyBusy(session),
            () => ShowSafetyWarning("safety.hotkeyBusy", _hotkey));

        using var current = Process.GetCurrentProcess();
        _watchdog = new WatchdogProcess(
            paths.WatchdogPath,
            new WatchdogArguments(
                current.Id,
                current.StartTime.ToFileTimeUtc(),
                session,
                paths.InstallDir,
                paths.UserDir,
                _hotkey.ToString()),
            log);
        if (!_watchdog.Start())
        {
            ShowSafetyWarning("safety.noWatchdog");
        }
    }

    /// <summary>Вызывается вне потока интерфейса: он может быть занят. Если он завис, программу завершит сторож.</summary>
    private void OnEmergencyStop()
    {
        _log?.Warn("Аварийная остановка");
        _host?.Save();
        Dispatcher.BeginInvoke(() => Shutdown());
    }

    private void ShowSafetyWarning(string key, params object[] args) => Dispatcher.BeginInvoke(() =>
    {
        if (_designReady)
        {
            ToastManager.Show(ToastKind.Danger, _host!.Texts.Get($"{key}.title"), _host.Texts.Get($"{key}.text", args));
        }
    });

    private bool InstallDesignSystem(AppPaths paths, FileLog log)
    {
        try
        {
            PixelAssets.Install(Resources, paths.AssetsDir, _host!.Texts);
            return true;
        }
        catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
        {
            log.Error("Не удалось загрузить ресурсы интерфейса", exception);
            return false;
        }
    }

    private void OpenShowcase()
    {
        if (_showcase is null)
        {
            _showcase = new ShowcaseWindow();
            _showcase.Closed += (_, _) => _showcase = null;
            _showcase.Show();
        }

        _showcase.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mouseHook?.Dispose();
        _lockScreen?.Dispose();
        _extensionScreen?.Dispose();
        _presenceScreen?.Dispose();
        _shopScreen?.Dispose();
        _statsHotkey?.Dispose();
        _statsScreen?.Dispose();
        _questScreen?.Dispose();
        _progressScreen?.Dispose();
        _hardshipScreen?.Dispose();
        _eventScreen?.Dispose();
        _cosmeticsScreen?.Dispose();
        _hud?.Dispose();
        _tray?.Dispose();
        _watchdog?.Dispose();
        _host?.Dispose();
        _emergencyStop?.Dispose();
        _hotkeyBusy?.Dispose();
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        _log?.Error("Необработанная ошибка в потоке интерфейса", e.Exception);

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e) =>
        _log?.Error("Необработанная ошибка", e.ExceptionObject as Exception);

    private void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        _log?.Error("Необработанная ошибка в фоновой задаче", e.Exception);
}
