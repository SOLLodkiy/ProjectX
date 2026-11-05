using System.ComponentModel;
using System.Diagnostics;
using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Safety;
using ADHDWindows.Platform;

namespace ADHDWindows.Watchdog;

/// <summary>
/// Сторожевой процесс. Держит аварийную горячую клавишу, поэтому она работает и при зависшей программе.
/// Когда программа завершается по любой причине, откатывает изменения системы, оставшиеся от неё.
/// </summary>
internal static class Program
{
    private const int InvalidArguments = 2;

    /// <summary>Столько программа может закрываться сама: она сохраняет данные и снимает эффекты модулей.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(5);

    private static int Main(string[] args)
    {
        if (WatchdogArguments.Parse(args) is not { } options)
        {
            return InvalidArguments;
        }

        var paths = new AppPaths(options.InstallDir, options.UserDir);
        var log = new FileLog(paths.LogsDir, "watchdog-");
        log.Info($"Сторож запущен, PID {Environment.ProcessId}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error("Необработанная ошибка сторожа", e.ExceptionObject as Exception);

        using (var app = OpenApp(options))
        {
            if (app is null)
            {
                log.Warn("Программа уже завершилась");
            }
            else
            {
                Watch(app, options, log);
            }
        }

        var recovery = new RecoveryService(
            new ChangeJournal(paths.RecoveryPath, log),
            () => ModuleLoader.LoadRestorers(paths.ModulesDir, log),
            log);
        recovery.RestoreSession(options.Session);
        log.Info("Сторож завершён");
        return 0;
    }

    private static Process? OpenApp(WatchdogArguments options)
    {
        try
        {
            var process = Process.GetProcessById(options.ProcessId);
            if (process.StartTime.ToFileTimeUtc() == options.ProcessStarted)
            {
                return process;
            }

            process.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void Watch(Process app, WatchdogArguments options, ILog log)
    {
        var pressed = new TaskCompletionSource();
        if (!Hotkey.TryParse(options.Hotkey, out var hotkey))
        {
            log.Warn($"Неверное сочетание клавиш: {options.Hotkey}");
        }

        using var listener = new GlobalHotkey(hotkey, () => pressed.TrySetResult(), log);
        if (!listener.Start())
        {
            NamedSignal.Raise(ScopedName.HotkeyBusy(options.Session));
        }

        var exited = app.WaitForExitAsync();
        if (Task.WaitAny(exited, pressed.Task) == 1)
        {
            EmergencyStop(app, options, log);
        }
        else
        {
            log.Info($"Программа завершилась, код {app.ExitCode}");
        }
    }

    private static void EmergencyStop(Process app, WatchdogArguments options, ILog log)
    {
        log.Warn("Аварийная остановка");
        NamedSignal.Raise(ScopedName.EmergencyStop(options.Session));
        if (app.WaitForExit(ShutdownGrace))
        {
            return;
        }

        log.Warn("Программа не ответила и будет завершена принудительно");
        try
        {
            app.Kill();
            app.WaitForExit(KillTimeout);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            log.Error("Не удалось завершить программу", exception);
        }
    }
}
