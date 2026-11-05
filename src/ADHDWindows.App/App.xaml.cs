using System.Windows;
using System.Windows.Threading;
using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Logging;
using ADHDWindows.Platform;

namespace ADHDWindows.App;

public partial class App : Application
{
    private FileLog? _log;
    private SingleInstanceGuard? _instanceGuard;
    private GameHost? _host;
    private TrayIcon? _tray;

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
        _tray = new TrayIcon(_host.Texts, options.DeveloperMode, onExit: () => Shutdown());
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
