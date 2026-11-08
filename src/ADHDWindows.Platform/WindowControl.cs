using System.Runtime.InteropServices;
using System.Text;

namespace ADHDWindows.Platform;

/// <summary>Окно программы. <paramref name="ProcessId"/> — настоящий владелец, в том числе у приложений Магазина.</summary>
public sealed record WindowInfo(IntPtr Handle, int ProcessId, string ClassName, bool IsMinimized);

/// <summary>Окна верхнего уровня других программ.</summary>
public static class WindowControl
{
    private const int ExtendedStyleIndex = -20;
    private const long ToolWindow = 0x00000080;
    private const uint OwnerRelation = 4;
    private const uint RootAncestor = 2;
    private const int Cloaked = 14;
    private const int Minimize = 6;
    private const int Restore = 9;
    private const int ForceMinimizeCommand = 11;

    // Рамка приложений Магазина принадлежит общему системному процессу, а настоящий владелец — у вложенного окна.
    private const string FrameClass = "ApplicationFrameWindow";
    private const string CoreClass = "Windows.UI.Core.CoreWindow";

    public static IReadOnlyList<WindowInfo> AppWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows(
            (window, _) =>
            {
                if (Describe(window) is { } info)
                {
                    windows.Add(info);
                }

                return true;
            },
            IntPtr.Zero);
        return windows;
    }

    /// <summary>Сведения об окне, если это видимое окно программы, а не служебное.</summary>
    public static WindowInfo? Describe(IntPtr window)
    {
        if (!IsWindowVisible(window)
            || GetAncestor(window, RootAncestor) != window
            || GetWindow(window, OwnerRelation) != IntPtr.Zero
            || (GetWindowLongPtrW(window, ExtendedStyleIndex).ToInt64() & ToolWindow) != 0
            || IsCloaked(window))
        {
            return null;
        }

        var bounds = WindowTraits.Bounds(window);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        var name = ClassName(window);
        return new WindowInfo(window, OwnerOf(window, name), name, IsIconic(window));
    }

    /// <summary>Принадлежит ли окно процессу: система использует номера закрытых окон повторно.</summary>
    public static bool BelongsTo(IntPtr window, int processId) =>
        IsWindow(window) && OwnerOf(window, ClassName(window)) == processId;

    public static bool IsMinimized(IntPtr window) => IsIconic(window);

    /// <summary>Заголовок окна. Для зависшего окна система отвечает сама, не дожидаясь программы.</summary>
    public static string Title(IntPtr window)
    {
        var length = GetWindowTextLengthW(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder(length + 1);
        return GetWindowTextW(window, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    /// <summary>Просит окно свернуться. Не ждёт ответа: завис процесс или нет, вызов вернётся сразу.</summary>
    public static void RequestMinimize(IntPtr window) => ShowWindowAsync(window, Minimize);

    /// <summary>Сворачивает окно без участия его программы: годится для зависшей или замороженной.</summary>
    public static void ForceMinimize(IntPtr window) => ShowWindowAsync(window, ForceMinimizeCommand);

    public static void RestoreWindow(IntPtr window)
    {
        if (IsIconic(window))
        {
            ShowWindowAsync(window, Restore);
        }
    }

    private static int OwnerOf(IntPtr window, string className)
    {
        GetWindowThreadProcessId(window, out var owner);
        if (className != FrameClass)
        {
            return owner;
        }

        var hosted = owner;
        EnumChildWindows(
            window,
            (child, _) =>
            {
                GetWindowThreadProcessId(child, out var childOwner);
                if (childOwner == owner || ClassName(child) != CoreClass)
                {
                    return true;
                }

                hosted = childOwner;
                return false;
            },
            IntPtr.Zero);
        return hosted;
    }

    private static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassNameW(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, Cloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumCallback callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint relation);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    private delegate bool EnumCallback(IntPtr window, IntPtr data);
}
