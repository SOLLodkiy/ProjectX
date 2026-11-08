using System.ComponentModel;
using System.Runtime.InteropServices;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Platform;

/// <summary>
/// Перехватывает движение мыши от устройства до того, как система сдвинет курсор. Обработчик получает текущее
/// положение и то, куда курсор сдвинулся бы, и возвращает null — пропустить, или точку, куда поставить курсор вместо
/// этого. Точка назначения не обрезается краем экрана, поэтому смещение верное и у края.
/// Движения от программ (в том числе от самого фильтра) пропускаются. Обработчик вызывается в потоке хука
/// при каждом движении и должен быть быстрым: система ждёт его ответа. Если программа зависнет, система
/// сама снимет хук по таймауту и мышь продолжит работать.
/// </summary>
public sealed class MouseMoveFilter(Func<ScreenPoint, ScreenPoint, ScreenPoint?> filter, ILog log) : IDisposable
{
    private const int MouseHookId = 14;
    private const int MouseMove = 0x0200;

    // Флаги LLMHF_INJECTED и LLMHF_LOWER_IL_INJECTED, смещение поля flags в MSLLHOOKSTRUCT.
    private const uint InjectedFlags = 0x01 | 0x02;
    private const int FlagsOffset = 12;

    private HookCallback? _callback;
    private Thread? _thread;
    private uint _threadId;
    private bool _stopped;

    /// <summary>Ставит хук. Возвращает false и пишет в журнал, если система отказала.</summary>
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
            Name = "MouseMoveFilter",
            Priority = ThreadPriority.Highest,
        };
        thread.Start();
        ready.Wait();
        if (!installed)
        {
            thread.Join();
            return false;
        }

        _thread = thread;
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
    }

    private void Run(ManualResetEventSlim ready, Action<bool> report)
    {
        _threadId = NativeMessages.GetCurrentThreadId();
        NativeMessages.EnsureQueue();
        _callback = OnHook;
        var hook = SetWindowsHookExW(MouseHookId, _callback, GetModuleHandleW(null), 0);
        if (hook == IntPtr.Zero)
        {
            log.Error("Не удалось поставить фильтр движения мыши", new Win32Exception(Marshal.GetLastWin32Error()));
            report(false);
            ready.Set();
            return;
        }

        report(true);
        ready.Set();
        while (NativeMessages.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMessages.DispatchMessageW(ref message);
        }

        UnhookWindowsHookEx(hook);
    }

    private IntPtr OnHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (int)message == MouseMove && ((uint)Marshal.ReadInt32(data, FlagsOffset) & InjectedFlags) == 0)
        {
            try
            {
                var target = new ScreenPoint(Marshal.ReadInt32(data, 0), Marshal.ReadInt32(data, 4));
                var current = Desktop.CursorPosition();
                if (filter(current, target) is { } replacement)
                {
                    if (replacement != current)
                    {
                        CursorControl.MoveTo(replacement);
                    }

                    // Ненулевой ответ отменяет исходное движение.
                    return new IntPtr(1);
                }
            }
            catch (Exception exception)
            {
                log.Error("Ошибка фильтра движения мыши", exception);
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, message, data);
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
