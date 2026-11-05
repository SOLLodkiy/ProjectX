using System.ComponentModel;
using System.Runtime.InteropServices;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Safety;

namespace ADHDWindows.Platform;

/// <summary>
/// Глобальная горячая клавиша. RegisterHotKey обрабатывается системой, поэтому в отличие от хука клавиатуры
/// срабатывает и при активном окне с повышенными правами. Обработчик вызывается в отдельном потоке.
/// </summary>
public sealed class GlobalHotkey(Hotkey hotkey, Action onPressed, ILog log) : IDisposable
{
    private const int Id = 1;
    private const uint HotkeyMessage = 0x0312;
    private const uint NoRepeat = 0x4000;

    private Thread? _thread;
    private uint _threadId;
    private bool _stopped;

    /// <summary>Возвращает false, если сочетание занято другой программой.</summary>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        if (_thread is not null)
        {
            return true;
        }

        using var ready = new ManualResetEventSlim();
        var registered = false;
        var thread = new Thread(() => Run(ready, value => registered = value))
        {
            IsBackground = true,
            Name = "GlobalHotkey",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
        ready.Wait();
        if (!registered)
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

        if (!RegisterHotKey(IntPtr.Zero, Id, (uint)hotkey.Modifiers | NoRepeat, (uint)hotkey.VirtualKey))
        {
            log.Error($"Не удалось зарегистрировать {hotkey}", new Win32Exception(Marshal.GetLastWin32Error()));
            report(false);
            ready.Set();
            return;
        }

        report(true);
        ready.Set();
        log.Info($"Горячая клавиша зарегистрирована: {hotkey}");

        while (NativeMessages.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message == HotkeyMessage)
            {
                try
                {
                    onPressed();
                }
                catch (Exception exception)
                {
                    log.Error("Обработчик горячей клавиши завершился с ошибкой", exception);
                }
            }
        }

        UnregisterHotKey(IntPtr.Zero, Id);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
