using System.Security.Cryptography;
using System.Text;

namespace ADHDWindows.Core.Safety;

/// <summary>Имена объектов системы, разделяемых процессами программы. В них нельзя использовать путь напрямую.</summary>
public static class ScopedName
{
    private const int HashLength = 16;

    public static string ForPath(string prefix, string path)
    {
        var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant());
        return $@"Local\{prefix}.{Convert.ToHexString(SHA256.HashData(bytes))[..HashLength]}";
    }

    public static string EmergencyStop(Guid session) => $@"Local\ADHDWindows.EmergencyStop.{session:N}";

    public static string HotkeyBusy(Guid session) => $@"Local\ADHDWindows.HotkeyBusy.{session:N}";
}
