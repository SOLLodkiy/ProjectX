using System.Runtime.InteropServices;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Platform;

/// <summary>
/// Сообщает о появлении и активации окон. Использует события Windows вне контекста чужого процесса,
/// поэтому ничего в него не внедряет. Обработчик вызывается в потоке наблюдателя и не должен его задерживать.
/// Второй аргумент обработчика равен true, если пользователь переключился на окно.
/// </summary>
public sealed class WindowWatcher(Action<IntPtr, bool> handler, ILog log) : IDisposable
{
    private const uint ForegroundChanged = 0x0003;
    private const uint MinimizeEnded = 0x0017;
    private const uint ObjectShown = 0x8002;
    private const uint OutOfContext = 0x0000;
    private const uint SkipOwnProcess = 0x0002;
    private const int WholeWindow = 0;

    private WinEventCallback? _callback;
    private Thread? _thread;
    private uint _threadId;
    private bool _stopped;

    /// <summary>Подключается к событиям. Если система отказала, остаётся только периодический осмотр окон.</summary>
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
            Name = "WindowWatcher",
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

        // Делегат хранится в поле: сборщик мусора не должен удалить его, пока им пользуется система.
        _callback = OnEvent;
        IntPtr[] hooks = [Hook(ObjectShown), Hook(ForegroundChanged), Hook(MinimizeEnded)];
        var installed = Array.TrueForAll(hooks, hook => hook != IntPtr.Zero);
        report(installed);
        ready.Set();
        if (!installed)
        {
            log.Warn("Не удалось подключиться к событиям окон");
            Unhook(hooks);
            return;
        }

        while (NativeMessages.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMessages.DispatchMessageW(ref message);
        }

        Unhook(hooks);
    }

    private IntPtr Hook(uint kind) =>
        SetWinEventHook(kind, kind, IntPtr.Zero, _callback!, 0, 0, OutOfContext | SkipOwnProcess);

    private static void Unhook(IntPtr[] hooks)
    {
        foreach (var hook in hooks)
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWinEvent(hook);
            }
        }
    }

    private void OnEvent(IntPtr hook, uint kind, IntPtr window, int target, int child, uint thread, uint time)
    {
        if (target == WholeWindow && child == 0 && window != IntPtr.Zero)
        {
            handler(window, kind != ObjectShown);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint min,
        uint max,
        IntPtr module,
        WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    private delegate void WinEventCallback(IntPtr hook, uint kind, IntPtr window, int target, int child, uint thread, uint time);
}
