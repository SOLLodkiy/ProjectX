using System.Runtime.InteropServices;

namespace ADHDWindows.Platform;

/// <summary>Свойства окон, недоступные через WPF.</summary>
public static class WindowTraits
{
    private const int ExtendedStyleIndex = -20;
    private const long ToolWindow = 0x00000080;
    private const long NoActivate = 0x08000000;

    /// <summary>Окно не получает фокус при показе и клике и не попадает в Alt+Tab.</summary>
    public static void MakeNonActivating(IntPtr window)
    {
        var style = GetWindowLongPtrW(window, ExtendedStyleIndex).ToInt64();
        SetWindowLongPtrW(window, ExtendedStyleIndex, new IntPtr(style | ToolWindow | NoActivate));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
}
