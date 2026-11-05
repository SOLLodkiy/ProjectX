using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Platform;

/// <summary>
/// Глобальный хук мыши. Система ждёт обработчик хука при каждом событии мыши, поэтому он работает
/// в отдельном потоке, только записывает событие в очередь и не зависит от загрузки интерфейса.
/// Подписчик вызывается из очереди, в отдельной задаче, и не влияет на задержку мыши.
/// </summary>
public sealed class MouseHook(Action<MouseButtonChanged> handler, ILog log) : IDisposable
{
    public const int LeftDown = 0x0201;
    public const int LeftUp = 0x0202;
    public const int RightDown = 0x0204;
    public const int RightUp = 0x0205;
    public const int MiddleDown = 0x0207;
    public const int MiddleUp = 0x0208;

    /// <summary>Флаг LLMHF_INJECTED.</summary>
    public const uint InjectedFlag = 0x01;

    /// <summary>Флаг LLMHF_LOWER_IL_INJECTED.</summary>
    public const uint LowerIntegrityInjectedFlag = 0x02;

    private const int MouseHookId = 14;

    // Смещение поля flags в MSLLHOOKSTRUCT: POINT занимает 8 байт, mouseData 4.
    private const int FlagsOffset = 12;

    // Очередь нужна на случай занятого подписчика. Устаревшие клики важны меньше, чем отсутствие задержки.
    private const int QueueCapacity = 1024;

    private readonly Channel<MouseButtonChanged> _queue = Channel.CreateBounded<MouseButtonChanged>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    private HookCallback? _callback;
    private Thread? _thread;
    private Task? _dispatcher;
    private uint _threadId;
    private bool _stopped;

    /// <summary>Ставит хук. Если система отказала, пишет об этом в журнал и возвращает false: программа работает дальше без кликов.</summary>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        if (_thread is not null)
        {
            return true;
        }

        using var ready = new ManualResetEventSlim();
        var installed = false;
        var thread = new Thread(() => Run(ready, value => installed = value))
        {
            IsBackground = true,
            Name = "MouseHook",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
        ready.Wait();
        if (!installed)
        {
            thread.Join();
            return false;
        }

        _thread = thread;
        _dispatcher = Task.Run(DispatchAsync);
        return true;
    }

    public void Dispose()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        if (_thread is not null)
        {
            NativeMessages.PostQuit(_threadId);
            _thread.Join();
        }

        _queue.Writer.TryComplete();
        _dispatcher?.GetAwaiter().GetResult();
    }

    /// <summary>Переводит сообщение хука в событие. Возвращает null для сообщений без смены кнопки.</summary>
    public static MouseButtonChanged? Translate(int message, uint flags, TimeSpan time)
    {
        var injected = (flags & (InjectedFlag | LowerIntegrityInjectedFlag)) != 0;
        return message switch
        {
            LeftDown => new MouseButtonChanged(MouseButton.Left, Pressed: true, injected, time),
            LeftUp => new MouseButtonChanged(MouseButton.Left, Pressed: false, injected, time),
            RightDown => new MouseButtonChanged(MouseButton.Right, Pressed: true, injected, time),
            RightUp => new MouseButtonChanged(MouseButton.Right, Pressed: false, injected, time),
            MiddleDown => new MouseButtonChanged(MouseButton.Middle, Pressed: true, injected, time),
            MiddleUp => new MouseButtonChanged(MouseButton.Middle, Pressed: false, injected, time),
            _ => null,
        };
    }

    private void Run(ManualResetEventSlim ready, Action<bool> report)
    {
        _threadId = NativeMessages.GetCurrentThreadId();
        NativeMessages.EnsureQueue();

        // Делегат хранится в поле: сборщик мусора не должен удалить его, пока им пользуется система.
        _callback = OnHook;
        var hook = SetWindowsHookExW(MouseHookId, _callback, GetModuleHandleW(null), 0);
        if (hook == IntPtr.Zero)
        {
            log.Error("Не удалось поставить хук мыши", new Win32Exception(Marshal.GetLastWin32Error()));
            report(false);
            ready.Set();
            return;
        }

        report(true);
        ready.Set();
        log.Info("Хук мыши поставлен");

        while (NativeMessages.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMessages.DispatchMessageW(ref message);
        }

        UnhookWindowsHookEx(hook);
        log.Info("Хук мыши снят");
    }

    private IntPtr OnHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var kind = (int)message;

            // Движение мыши идёт чаще всего, поэтому отсеивается до чтения данных.
            if (kind is >= LeftDown and <= MiddleUp)
            {
                var flags = (uint)Marshal.ReadInt32(data, FlagsOffset);
                if (Translate(kind, flags, Stopwatch.GetElapsedTime(0)) is { } change)
                {
                    _queue.Writer.TryWrite(change);
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private async Task DispatchAsync()
    {
        await foreach (var change in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                handler(change);
            }
            catch (Exception exception)
            {
                log.Error("Обработчик событий мыши завершился с ошибкой", exception);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, HookCallback callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
}
