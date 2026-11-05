using System.Runtime.InteropServices;

namespace ADHDWindows.Platform;

/// <summary>Очередь сообщений потока: через неё хук и горячая клавиша получают события от системы.</summary>
internal static class NativeMessages
{
    private const uint Quit = 0x0012;
    private const uint UserMessage = 0x0400;
    private const uint NoRemove = 0;

    /// <summary>Очередь создаётся при первом обращении, иначе сигнал остановки может потеряться.</summary>
    public static void EnsureQueue() => PeekMessageW(out _, IntPtr.Zero, UserMessage, UserMessage, NoRemove);

    public static void PostQuit(uint threadId) => PostThreadMessageW(threadId, Quit, IntPtr.Zero, IntPtr.Zero);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out NativeMessage message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref NativeMessage message);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMessage
{
    public IntPtr Window;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int X;
    public int Y;
    public uint Private;
}
