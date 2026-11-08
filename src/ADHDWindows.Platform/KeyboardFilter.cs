using System.ComponentModel;
using System.Runtime.InteropServices;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Platform;

/// <summary>Нажатие или отпускание клавиши на настоящей клавиатуре.</summary>
/// <param name="VirtualKey">Код клавиши Windows (VK_*).</param>
/// <param name="ScanCode">Код положения клавиши: от него зависит буква в раскладке.</param>
/// <param name="Down">Нажатие или автоповтор; false — отпускание.</param>
public readonly record struct KeyStroke(int VirtualKey, int ScanCode, bool Down, bool Extended);

/// <summary>
/// Перехватывает нажатия клавиатуры до программ. Обработчик решает, проглотить ли нажатие (true) или пропустить.
/// Нажатия от программ (в том числе ввод самого фильтра) пропускаются всегда. Обработчик вызывается в потоке
/// хука и должен быть быстрым: система ждёт ответа. Если программа зависнет или завершится, система снимет хук,
/// и клавиатура сразу работает как обычно.
/// </summary>
public sealed class KeyboardFilter(Func<KeyStroke, bool> filter, ILog log) : IDisposable
{
    private const int KeyboardHookId = 13;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const int SystemKeyDown = 0x0104;
    private const int SystemKeyUp = 0x0105;

    // KBDLLHOOKSTRUCT: vkCode, scanCode, flags. Флаги LLKHF_EXTENDED, LLKHF_LOWER_IL_INJECTED, LLKHF_INJECTED.
    private const uint ExtendedFlag = 0x01;
    private const uint InjectedFlags = 0x02 | 0x10;

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
            Name = "KeyboardFilter",
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

    /// <summary>Зажат ли Ctrl, Alt или Win: сочетания клавиш усложнения клавиатуры не трогают.</summary>
    public static bool ShortcutModifierDown() =>
        IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);

    /// <summary>Зажат ли Shift.</summary>
    public static bool ShiftDown() => IsDown(0x10);

    /// <summary>Включён ли Caps Lock.</summary>
    public static bool CapsLockOn() => (GetKeyState(0x14) & 1) != 0;

    /// <summary>Язык раскладки активного окна (младшее слово HKL), например 0x0419 — русский, 0x0409 — английский.</summary>
    public static int ForegroundLanguage()
    {
        var window = GetForegroundWindow();
        var thread = window == IntPtr.Zero ? 0 : GetWindowThreadProcessId(window, out _);
        return (int)(GetKeyboardLayout(thread).ToInt64() & 0xFFFF);
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private void Run(ManualResetEventSlim ready, Action<bool> report)
    {
        _threadId = NativeMessages.GetCurrentThreadId();
        NativeMessages.EnsureQueue();
        _callback = OnHook;
        var hook = SetWindowsHookExW(KeyboardHookId, _callback, GetModuleHandleW(null), 0);
        if (hook == IntPtr.Zero)
        {
            log.Error("Не удалось поставить фильтр клавиатуры", new Win32Exception(Marshal.GetLastWin32Error()));
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
        var kind = (int)message;
        if (code >= 0 && kind is KeyDown or KeyUp or SystemKeyDown or SystemKeyUp)
        {
            var flags = (uint)Marshal.ReadInt32(data, 8);
            if ((flags & InjectedFlags) == 0)
            {
                try
                {
                    var stroke = new KeyStroke(
                        Marshal.ReadInt32(data, 0),
                        Marshal.ReadInt32(data, 4),
                        kind is KeyDown or SystemKeyDown,
                        (flags & ExtendedFlag) != 0);
                    if (filter(stroke))
                    {
                        return new IntPtr(1);
                    }
                }
                catch (Exception exception)
                {
                    log.Error("Ошибка фильтра клавиатуры", exception);
                }
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint thread);

    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
}

/// <summary>Ввод с клавиатуры от имени программы. Фильтры клавиатуры такой ввод пропускают.</summary>
public static class KeyboardInput
{
    public const int Backspace = 0x08;
    public const int Enter = 0x0D;

    private const uint KeyboardType = 1;
    private const uint KeyUpFlag = 0x0002;
    private const uint UnicodeFlag = 0x0004;

    /// <summary>Печатает символ в окно с фокусом, независимо от раскладки.</summary>
    public static void Type(char character) =>
        Send([Key(0, character, UnicodeFlag), Key(0, character, UnicodeFlag | KeyUpFlag)]);

    /// <summary>Нажимает и отпускает клавишу, например <see cref="Backspace"/> или <see cref="Enter"/>.</summary>
    public static void Press(int virtualKey) =>
        Send([Key((ushort)virtualKey, '\0', 0), Key((ushort)virtualKey, '\0', KeyUpFlag)]);

    private static Input Key(ushort virtualKey, char character, uint flags) => new()
    {
        Type = KeyboardType,
        Keyboard = new KeyboardInputData { VirtualKey = virtualKey, ScanCode = character, Flags = flags },
    };

    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public KeyboardInputData Keyboard;

        // INPUT — объединение; ввод мыши длиннее ввода клавиатуры, поэтому структура дополняется до его размера.
        public long Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }
}
