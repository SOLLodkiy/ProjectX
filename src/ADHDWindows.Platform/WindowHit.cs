using System.Runtime.InteropServices;
using ADHDWindows.Core.Display;

namespace ADHDWindows.Platform;

/// <summary>Какое окно находится в точке экрана.</summary>
public static class WindowHit
{
    private const uint RootAncestor = 2;

    /// <summary>Окно верхнего уровня в точке. Окна, сквозь которые проходит мышь (оверлеи), пропускаются системой.</summary>
    public static IntPtr TopWindowAt(ScreenPoint point)
    {
        var window = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
        return window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(window, RootAncestor);
    }

    /// <summary>Высота заголовка окна с рамкой в пикселях экрана: по ней валун понимает, что попал в заголовок.</summary>
    public static int CaptionHeight() =>
        GetSystemMetrics(4) + GetSystemMetrics(33) + GetSystemMetrics(92) + 8;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
