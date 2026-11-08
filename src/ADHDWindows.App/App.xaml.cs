using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.App.Hud;
using ADHDWindows.App.Locks;
using ADHDWindows.App.Shop;
using ADHDWindows.App.Time;
using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Safety;
using ADHDWindows.Platform;

namespace ADHDWindows.App;

public partial class App : Application
{
    private const string DeveloperGrantSource = "developer";
    private const int DeveloperGrant = 1000;
    private const string ManualProbeToken = "tray";

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
        _host.Start(() => StartWatchdog(paths, log));
        _mouseHook = new MouseHook(_host.Events.Publish, log);
        _mouseHook.Start();

        _designReady = InstallDesignSystem(paths, log);
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
                () => _shopScreen.Open(),
                () => _lockScreen.Open(),
                () => Shutdown(),
                _hotkey.ToString()));

        if (hudSettings is not null)
        {
            _hud = new HudWindow(_host.Economy, _host.Events, _host.Texts, hudSettings, log);
            _hud.ConnectShop(_shopScreen.Open, () => _shopScreen.Available);
            _hud.Start();
        }

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
