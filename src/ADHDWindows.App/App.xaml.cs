using System.IO;
using System.Windows;
using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.App.Hud;
using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Logging;
using ADHDWindows.Platform;

namespace ADHDWindows.App;

public partial class App : Application
{
    private const string DeveloperGrantSource = "developer";
    private const int DeveloperGrant = 1000;

    private FileLog? _log;
    private SingleInstanceGuard? _instanceGuard;
    private GameHost? _host;
    private TrayIcon? _tray;
    private HudWindow? _hud;
    private ShowcaseWindow? _showcase;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.ForCurrentUser();
        var log = new FileLog(paths.LogsDir);
        _log = log;
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;

        _instanceGuard = new SingleInstanceGuard(SingleInstanceGuard.ApplicationName);
        if (!_instanceGuard.TryAcquire())
        {
            log.Info("Программа уже запущена, вторая копия закрывается");
            Shutdown();
            return;
        }

        var options = AppOptions.Parse(e.Args);
        log.Info($"Запуск, режим разработчика: {options.DeveloperMode}");
        _host = new GameHost(paths, options, log);
        _host.Start();

        var designReady = InstallDesignSystem(paths, log);
        var hudSettings = designReady ? new HudSettings(_host.Settings) : null;
        _tray = new TrayIcon(
            _host.Texts,
            Path.Combine(paths.AssetsDir, "app.ico"),
            options.DeveloperMode,
            new TrayActions(
                hudSettings,
                designReady ? OpenShowcase : null,
                () => _host.Economy.Earn(DeveloperGrantSource, DeveloperGrant),
                () => Shutdown()));

        if (hudSettings is not null)
        {
            _hud = new HudWindow(_host.Economy, _host.Events, _host.Texts, hudSettings, log);
            _hud.Start();
        }

        if (designReady && options.OpenShowcase)
        {
            OpenShowcase();
        }
    }

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
        _hud?.Dispose();
        _tray?.Dispose();
        _host?.Dispose();
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
