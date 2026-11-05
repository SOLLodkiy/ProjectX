using System.Runtime.InteropServices;
using ADHDWindows.Core.Display;

namespace ADHDWindows.Platform;

/// <summary>Свойства окон, недоступные через WPF.</summary>
public static class WindowTraits
{
    private const int ExtendedStyleIndex = -20;
    private const long Transparent = 0x00000020;
    private const long ToolWindow = 0x00000080;
    private const long NoActivate = 0x08000000;

    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivateMove = 0x0010;

    /// <summary>Окно не получает фокус при показе и клике и не попадает в Alt+Tab.</summary>
    public static void MakeNonActivating(IntPtr window) => ChangeStyle(window, ToolWindow | NoActivate, enable: true);

    /// <summary>Включает пропуск мыши: клики, наведение и колёсико достаются окну под этим.</summary>
    public static void SetClickThrough(IntPtr window, bool enabled) => ChangeStyle(window, Transparent, enabled);

    /// <summary>Двигает окно в пикселях экрана. Координаты WPF для этого не годятся: на разных мониторах масштаб разный.</summary>
    public static void Move(IntPtr window, ScreenPoint position) =>
        SetWindowPos(window, IntPtr.Zero, position.X, position.Y, 0, 0, NoSize | NoZOrder | NoActivateMove);

    /// <summary>Границы окна в пикселях экрана.</summary>
    public static ScreenRect Bounds(IntPtr window)
    {
        GetWindowRect(window, out var rect);
        return new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static void ChangeStyle(IntPtr window, long flags, bool enable)
    {
        var style = GetWindowLongPtrW(window, ExtendedStyleIndex).ToInt64();
        var updated = enable ? style | flags : style & ~flags;
        if (updated != style)
        {
            SetWindowLongPtrW(window, ExtendedStyleIndex, new IntPtr(updated));
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
