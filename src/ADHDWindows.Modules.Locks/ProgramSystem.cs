using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Locks;

/// <summary>Всё, что движок блокировки просит у Windows. В тестах подменяется.</summary>
public interface IProgramSystem
{
    IReadOnlyList<WindowInfo> Windows();

    WindowInfo? Describe(IntPtr window);

    IReadOnlyList<ProcessStamp> Processes();

    ProcessStamp? Stamp(int processId);

    ProgramFile? ReadFile(ProcessStamp process);

    bool IsAlive(ProcessStamp process);

    bool CanFreeze(ProcessStamp process);

    bool Freeze(ProcessStamp process);

    void Thaw(ProcessStamp process);

    /// <summary>Сворачивает окна и недолго ждёт: пока программа не заморожена, она делает это сама.</summary>
    void MinimizeAndWait(IReadOnlyList<IntPtr> windows);

    void ForceMinimize(IntPtr window);

    void Restore(IntPtr window);
}

public sealed class WindowsProgramSystem : IProgramSystem
{
    private static readonly TimeSpan MinimizeTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MinimizePoll = TimeSpan.FromMilliseconds(20);

    private readonly Dictionary<(string Path, DateTime Written), ProgramFile> _files = [];

    public IReadOnlyList<WindowInfo> Windows() => WindowControl.AppWindows();

    public WindowInfo? Describe(IntPtr window) => WindowControl.Describe(window);

    public IReadOnlyList<ProcessStamp> Processes() => ProcessControl.All();

    public ProcessStamp? Stamp(int processId) => ProcessControl.Stamp(processId);

    public ProgramFile? ReadFile(ProcessStamp process)
    {
        var path = ProcessControl.ImagePath(process);
        if (path is null)
        {
            return null;
        }

        try
        {
            var key = (path, File.GetLastWriteTimeUtc(path));
            if (!_files.TryGetValue(key, out var file))
            {
                file = ProgramFile.Read(path);
                _files[key] = file;
            }

            return file;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool IsAlive(ProcessStamp process) => ProcessControl.IsAlive(process);

    public bool CanFreeze(ProcessStamp process) => ProcessControl.CanFreeze(process);

    public bool Freeze(ProcessStamp process) => ProcessControl.Freeze(process);

    public void Thaw(ProcessStamp process) => ProcessControl.Thaw(process);

    public void MinimizeAndWait(IReadOnlyList<IntPtr> windows)
    {
        foreach (var window in windows)
        {
            WindowControl.RequestMinimize(window);
        }

        var deadline = DateTime.UtcNow + MinimizeTimeout;
        while (DateTime.UtcNow < deadline && !windows.All(WindowControl.IsMinimized))
        {
            Thread.Sleep(MinimizePoll);
        }

        foreach (var window in windows.Where(window => !WindowControl.IsMinimized(window)))
        {
            WindowControl.ForceMinimize(window);
        }
    }

    public void ForceMinimize(IntPtr window) => WindowControl.ForceMinimize(window);

    public void Restore(IntPtr window) => WindowControl.RestoreWindow(window);
}
