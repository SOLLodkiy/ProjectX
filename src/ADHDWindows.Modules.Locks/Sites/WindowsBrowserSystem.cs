using System.Diagnostics;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Locks.Sites;

public sealed class WindowsBrowserSystem : IBrowserSystem
{
    public string Title(IntPtr window) => WindowControl.Title(window);

    public string? CommandLine(ProcessStamp process) => ProcessControl.CommandLine(process);

    public string? ImagePath(ProcessStamp process) => ProcessControl.ImagePath(process);

    public void Launch(string path, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        Process.Start(info)?.Dispose();
    }
}
