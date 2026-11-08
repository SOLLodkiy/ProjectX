using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ADHDWindows.Platform;

/// <summary>Процесс. Время запуска отличает его от другого процесса, которому система выдала тот же номер.</summary>
public readonly record struct ProcessStamp(int Id, long StartTime);

/// <summary>Сведения о процессах и их заморозка. В чужие процессы ничего не внедряется.</summary>
public static class ProcessControl
{
    private const uint SuspendResume = 0x0800;
    private const uint QueryLimited = 0x1000;
    private const uint StillActive = 259;
    private const int MaxPath = 32768;

    public static ProcessStamp? Stamp(int processId)
    {
        using var handle = OpenProcess(QueryLimited, false, processId);
        return !handle.IsInvalid && GetProcessTimes(handle, out var created, out _, out _, out _)
            ? new ProcessStamp(processId, created)
            : null;
    }

    /// <summary>Все процессы, сведения о которых доступны. Системные могут отсутствовать.</summary>
    public static IReadOnlyList<ProcessStamp> All()
    {
        var stamps = new List<ProcessStamp>();
        foreach (var process in Process.GetProcesses())
        {
            if (Stamp(process.Id) is { } stamp)
            {
                stamps.Add(stamp);
            }

            process.Dispose();
        }

        return stamps;
    }

    public static bool IsAlive(ProcessStamp stamp)
    {
        using var handle = Open(stamp, QueryLimited);
        return handle is not null && GetExitCodeProcess(handle, out var code) && code == StillActive;
    }

    public static string? ImagePath(ProcessStamp stamp)
    {
        using var handle = Open(stamp, QueryLimited);
        if (handle is null)
        {
            return null;
        }

        var path = new StringBuilder(MaxPath);
        var length = path.Capacity;
        return QueryFullProcessImageNameW(handle, 0, path, ref length) ? path.ToString() : null;
    }

    /// <summary>Хватает ли прав на заморозку. Процессы с повышенными правами и защищённые вернут false.</summary>
    public static bool CanFreeze(ProcessStamp stamp)
    {
        using var handle = Open(stamp, SuspendResume);
        return handle is not null;
    }

    /// <summary>Останавливает все потоки процесса. Его память остаётся на месте, поэтому данные не теряются.</summary>
    public static bool Freeze(ProcessStamp stamp)
    {
        using var handle = Open(stamp, SuspendResume);
        return handle is not null && NtSuspendProcess(handle) == 0;
    }

    /// <summary>Безопасно при повторе: незамороженный процесс не меняется, а отсутствующий пропускается.</summary>
    public static bool Thaw(ProcessStamp stamp)
    {
        using var handle = Open(stamp, SuspendResume);
        return handle is not null && NtResumeProcess(handle) == 0;
    }

    /// <summary>Процесс, который запустил данный. Null, если родитель уже закрыт и его номер мог достаться другому.</summary>
    public static ProcessStamp? Parent(ProcessStamp stamp)
    {
        using var handle = Open(stamp, QueryLimited);
        if (handle is null
            || NtQueryInformationProcess(handle, BasicInformation, out var info, Marshal.SizeOf<BasicProcessInformation>(), out _) != 0)
        {
            return null;
        }

        // Родитель должен быть старше потомка, иначе это чужой процесс с освободившимся номером.
        return Stamp((int)info.ParentId.ToInt64()) is { } parent && parent.StartTime <= stamp.StartTime ? parent : null;
    }

    /// <summary>Командная строка процесса или null, если прав не хватило.</summary>
    public static string? CommandLine(ProcessStamp stamp)
    {
        using var handle = Open(stamp, QueryLimited);
        if (handle is null)
        {
            return null;
        }

        NtQueryInformationProcess(handle, CommandLineInformation, IntPtr.Zero, 0, out var size);
        if (size <= 0 || size > MaxCommandLineBytes)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(handle, CommandLineInformation, buffer, size, out _) != 0)
            {
                return null;
            }

            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            return text.Buffer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int BasicInformation = 0;
    private const int CommandLineInformation = 60;
    private const int MaxCommandLineBytes = 1 << 20;

    private static SafeProcessHandle? Open(ProcessStamp stamp, uint access)
    {
        var handle = OpenProcess(access | QueryLimited, false, stamp.Id);
        if (!handle.IsInvalid
            && GetProcessTimes(handle, out var created, out _, out _, out _)
            && created == stamp.StartTime)
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(SafeProcessHandle process);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int kind, out BasicProcessInformation info, int size, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int kind, IntPtr buffer, int size, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicProcessInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr ParentId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(SafeProcessHandle process);
}
