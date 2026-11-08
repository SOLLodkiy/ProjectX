using System.Runtime.InteropServices;

namespace ADHDWindows.Platform;

/// <summary>Назначение кнопок мыши: левая и правая могут быть поменяны местами в настройках системы.</summary>
public static class MouseButtons
{
    private const int SwapButtonMetric = 23;

    /// <summary>Поменяны ли сейчас левая и правая кнопки.</summary>
    public static bool IsSwapped() => GetSystemMetrics(SwapButtonMetric) != 0;

    /// <summary>
    /// Меняет кнопки до перезагрузки, не записывая выбор в профиль пользователя.
    /// Глобальный хук мыши после этого тоже видит кнопки поменянными.
    /// </summary>
    public static void SetSwapped(bool swapped) => SwapMouseButton(swapped);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SwapMouseButton([MarshalAs(UnmanagedType.Bool)] bool swap);
}
