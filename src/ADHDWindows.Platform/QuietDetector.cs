using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ADHDWindows.Platform;

/// <summary>
/// Ограничения безопасности для механик, мешающих работе: защищённый рабочий стол (UAC, вход в Windows, блокировка),
/// ввод пароля, полноэкранное приложение, презентация и «Фокусировка внимания», микрофон занят (созвон).
/// </summary>
public static class QuietDetector
{
    public const string SecureDesktop = "secure";
    public const string Password = "password";
    public const string Fullscreen = "fullscreen";
    public const string Presentation = "presentation";
    public const string Call = "call";

    private const uint ReadObjects = 0x0001;
    private const int NameInfo = 2;
    private const long PasswordStyle = 0x0020;
    private const int StyleIndex = -16;

    private const string MicrophoneKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    // Окна системного запроса учётных данных: туда вводят пароль или PIN.
    private static readonly string[] CredentialClasses = ["Credential Dialog Xaml Host"];

    private static readonly TimeSpan MicrophoneCacheTime = TimeSpan.FromSeconds(5);
    private static readonly Lock MicrophoneLock = new();
    private static DateTime _microphoneChecked = DateTime.MinValue;
    private static bool _microphoneBusy;

    /// <summary>Код первой причины, по которой сейчас нельзя мешать человеку, или null.</summary>
    public static string? Check(bool watchCalls = true)
    {
        if (IsSecureDesktop())
        {
            return SecureDesktop;
        }

        if (IsPasswordFocused())
        {
            return Password;
        }

        if (Desktop.IsForegroundFullscreen())
        {
            return Fullscreen;
        }

        if (IsPresenting())
        {
            return Presentation;
        }

        return watchCalls && IsMicrophoneBusy() ? Call : null;
    }

    /// <summary>Ввод идёт не на обычный рабочий стол: окно UAC, экран входа или блокировки.</summary>
    public static bool IsSecureDesktop()
    {
        var desktop = OpenInputDesktop(0, false, ReadObjects);
        if (desktop == IntPtr.Zero)
        {
            // Защищённый рабочий стол закрыт для обычной программы.
            return true;
        }

        try
        {
            var name = new StringBuilder(64);
            return GetUserObjectInformationW(desktop, NameInfo, name, name.Capacity * 2, out _)
                && !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    /// <summary>Фокус в поле пароля обычного окна Windows или открыто системное окно учётных данных.</summary>
    public static bool IsPasswordFocused()
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info))
        {
            return false;
        }

        if (info.Focus != IntPtr.Zero && (GetWindowLongPtrW(info.Focus, StyleIndex).ToInt64() & PasswordStyle) != 0
            && ClassName(info.Focus).Contains("Edit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && CredentialClasses.Contains(ClassName(foreground));
    }

    /// <summary>Режим презентации, полноэкранное приложение Direct3D или «Фокусировка внимания».</summary>
    public static bool IsPresenting()
    {
        // 2 — занят (полноэкранное приложение), 3 — Direct3D во весь экран, 4 — презентация, 6 — тихие часы.
        return SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4 or 6;
    }

    /// <summary>
    /// Какая-то программа сейчас пишет с микрофона: скорее всего, это созвон. Windows отмечает это в реестре
    /// для значка микрофона в трее. Ответ кешируется на несколько секунд.
    /// </summary>
    public static bool IsMicrophoneBusy()
    {
        lock (MicrophoneLock)
        {
            var now = DateTime.UtcNow;
            if (now - _microphoneChecked < MicrophoneCacheTime)
            {
                return _microphoneBusy;
            }

            _microphoneChecked = now;
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(MicrophoneKey);
                _microphoneBusy = root is not null && AnyInUse(root, depth: 0);
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                _microphoneBusy = false;
            }

            return _microphoneBusy;
        }
    }

    private static bool AnyInUse(RegistryKey key, int depth)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name);
            if (child is null)
            {
                continue;
            }

            if (child.GetValue("LastUsedTimeStart") is long start && start > 0
                && child.GetValue("LastUsedTimeStop") is long stop && stop == 0)
            {
                return true;
            }

            // Обычные программы лежат на уровень глубже, в разделе NonPackaged.
            if (depth == 0 && name == "NonPackaged" && AnyInUse(child, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassNameW(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder info, int length, out int needed);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

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
