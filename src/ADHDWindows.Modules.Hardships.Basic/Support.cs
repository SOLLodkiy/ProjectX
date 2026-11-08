using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ADHDWindows.Core.Safety;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Basic;

/// <summary>Фоновый поток, который вызывает действие примерно 60 раз в секунду и передаёт прошедшие секунды.</summary>
internal sealed class Ticker : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    public Ticker(string name, Action<double> tick, Action<Exception> error, int intervalMs = 15)
    {
        _thread = new Thread(() =>
        {
            var watch = Stopwatch.StartNew();
            var last = watch.Elapsed;
            while (!_stop.Token.WaitHandle.WaitOne(intervalMs))
            {
                var now = watch.Elapsed;
                try
                {
                    // Долгая пауза (сон, зависание) не должна превращаться в один большой рывок.
                    tick(Math.Min((now - last).TotalSeconds, 0.1));
                }
                catch (Exception exception)
                {
                    error(exception);
                }

                last = now;
            }
        })
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(StopTimeout);
        }

        _stop.Dispose();
    }
}

/// <summary>Возвращает курсоры из схемы пользователя. Одна запись на всех, кто меняет вид курсора.</summary>
public sealed class CursorRestorer : ISystemRestorer
{
    public const string ChangeKind = "hardships.cursors";

    public string Kind => ChangeKind;

    public void Restore(string data) => CursorControl.RestoreCursors();
}

/// <summary>Возвращает скорость указателя, записанную до изменения.</summary>
public sealed class MouseSpeedRestorer : ISystemRestorer
{
    public const string ChangeKind = "hardships.mouseSpeed";

    public string Kind => ChangeKind;

    public void Restore(string data)
    {
        if (int.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var speed))
        {
            CursorControl.SetSpeed(speed);
        }
    }
}

/// <summary>Свёрнутое окно чужой программы. Процесс запоминается: система повторно использует номера закрытых окон.</summary>
public sealed record MinimizedWindow(long Handle, int ProcessId);

/// <summary>Разворачивает окна, свёрнутые усложнением, если они всё ещё свёрнуты и принадлежат тем же процессам.</summary>
public sealed class MinimizedWindowsRestorer : ISystemRestorer
{
    public const string ChangeKind = "hardships.windows";

    public string Kind => ChangeKind;

    public static string ToData(IEnumerable<MinimizedWindow> windows) => JsonSerializer.Serialize(windows.ToList());

    public void Restore(string data)
    {
        var windows = JsonSerializer.Deserialize<List<MinimizedWindow>>(data) ?? [];

        // В обратном порядке: окно, свёрнутое первым, окажется сверху, как было до усложнения.
        for (var i = windows.Count - 1; i >= 0; i--)
        {
            var window = new IntPtr(windows[i].Handle);
            if (WindowControl.BelongsTo(window, windows[i].ProcessId))
            {
                WindowControl.RestoreInBackground(window);
            }
        }
    }
}
