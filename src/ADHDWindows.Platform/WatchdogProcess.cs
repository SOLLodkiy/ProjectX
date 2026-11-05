using System.ComponentModel;
using System.Diagnostics;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Safety;

namespace ADHDWindows.Platform;

/// <summary>Запускает сторожевой процесс и перезапускает его, если он завершился раньше программы.</summary>
public sealed class WatchdogProcess(string executable, WatchdogArguments arguments, ILog log) : IDisposable
{
    private const int MaxRestarts = 5;

    private readonly Lock _lock = new();
    private Process? _process;
    private int _restarts;
    private bool _stopped;

    public bool Start()
    {
        lock (_lock)
        {
            return !_stopped && Launch();
        }
    }

    /// <summary>Перестаёт следить за сторожем. Сам он работает дальше: после выхода программы он завершится сам.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _stopped = true;
            _process?.Dispose();
            _process = null;
        }
    }

    private bool Launch()
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (var argument in arguments.ToList())
        {
            info.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += OnExited;
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            log.Error($"Не удалось запустить сторожевой процесс: {executable}", exception);
            process.Dispose();
            return false;
        }

        _process?.Dispose();
        _process = process;
        log.Info($"Сторожевой процесс запущен: {process.Id}");
        return true;
    }

    private void OnExited(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (_stopped || !ReferenceEquals(sender, _process))
            {
                return;
            }

            if (_restarts == MaxRestarts)
            {
                log.Error("Сторожевой процесс больше не перезапускается: слишком много сбоев");
                return;
            }

            _restarts++;
            log.Warn($"Сторожевой процесс завершился, перезапуск {_restarts}");
            Launch();
        }
    }
}
