using System.Runtime.InteropServices;
using System.Text;

namespace ADHDWindows.Platform;

/// <summary>Куда сейчас идёт ввод с клавиатуры.</summary>
/// <param name="Window">Активное окно верхнего уровня.</param>
/// <param name="ProcessId">Процесс активного окна.</param>
/// <param name="WindowClass">Класс активного окна.</param>
/// <param name="Focus">Элемент с фокусом внутри окна, если система его сообщила.</param>
/// <param name="FocusClass">Класс элемента с фокусом.</param>
/// <param name="Password">Обычное поле Windows с флагом пароля (ES_PASSWORD).</param>
public sealed record FocusTarget(IntPtr Window, int ProcessId, string WindowClass, IntPtr Focus, string FocusClass, bool Password);

/// <summary>Сведения об окне и поле, куда печатает человек.</summary>
public static class FocusInfo
{
    private const int StyleIndex = -16;
    private const long PasswordStyle = 0x0020;

    /// <summary>Активное окно и поле с фокусом. Null — активного окна нет (например, открыт защищённый рабочий стол).</summary>
    public static FocusTarget? Current()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return null;
        }

        var thread = GetWindowThreadProcessId(window, out var processId);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var focus = thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
        var focusClass = focus == IntPtr.Zero ? string.Empty : ClassOf(focus);
        var password = focus != IntPtr.Zero
            && focusClass.Contains("Edit", StringComparison.OrdinalIgnoreCase)
            && (GetWindowLongPtrW(focus, StyleIndex).ToInt64() & PasswordStyle) != 0;
        return new FocusTarget(window, processId, ClassOf(window), focus, focusClass, password);
    }

    private static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassNameW(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }
}
