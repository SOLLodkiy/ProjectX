using System.Runtime.InteropServices;
using System.Text;
using ADHDWindows.Core.Display;

namespace ADHDWindows.Platform;

/// <summary>Сведения о рабочем столе: мониторы, курсор, активное окно.</summary>
public static class Desktop
{
    /// <summary>Сообщение о смене мониторов или разрешения.</summary>
    public const int DisplayChangedMessage = 0x007E;

    /// <summary>Сообщение о смене параметров системы.</summary>
    public const int SettingChangedMessage = 0x001A;

    /// <summary>Значение wParam при <see cref="SettingChangedMessage"/>, если изменилась рабочая область.</summary>
    public const int WorkAreaSetting = 0x002F;

    private const uint PrimaryMonitor = 1;
    private const uint NearestMonitor = 2;
    private const int EffectiveDpi = 0;
    private const int DefaultDpi = 96;

    // У этих окон размер во весь экран, но это сам рабочий стол.
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    public static IReadOnlyList<ScreenArea> Screens()
    {
        var screens = new List<ScreenArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        return screens;

        bool Collect(IntPtr monitor, IntPtr context, IntPtr clip, IntPtr data)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                var dpi = GetDpiForMonitor(monitor, EffectiveDpi, out var x, out _) == 0 ? (int)x : DefaultDpi;
                screens.Add(new ScreenArea(
                    info.Monitor.ToRect(),
                    info.Work.ToRect(),
                    dpi,
                    (info.Flags & PrimaryMonitor) != 0));
            }

            return true;
        }
    }

    public static ScreenPoint CursorPosition()
    {
        GetCursorPos(out var point);
        return new ScreenPoint(point.X, point.Y);
    }

    /// <summary>Активное окно занимает весь монитор, включая панель задач.</summary>
    public static bool IsForegroundFullscreen()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || IsShell(window))
        {
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfoW(MonitorFromWindow(window, NearestMonitor), ref info)
            && WindowTraits.Bounds(window).Contains(info.Monitor.ToRect());
    }

    private static bool IsShell(IntPtr window)
    {
        var name = new StringBuilder(64);
        return GetClassNameW(window, name, name.Capacity) > 0 && ShellClasses.Contains(name.ToString());
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr device, IntPtr clip, MonitorCallback callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr context, IntPtr clip, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly ScreenRect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
