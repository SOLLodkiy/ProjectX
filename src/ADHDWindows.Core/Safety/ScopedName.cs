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

    /// <summary>Переменная окружения с другим именем канала браузера. Её наследует мост, запущенный браузером.</summary>
    public const string BrowserBridgeVariable = "ADHDWINDOWS_BRIDGE";

    /// <summary>Канал между программой и мостом браузера. Один на пользователя, проверки задают свой через переменную.</summary>
    public static string BrowserBridge()
    {
        var custom = Environment.GetEnvironmentVariable(BrowserBridgeVariable);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom.Trim();
        }

        var user = Encoding.UTF8.GetBytes($@"{Environment.UserDomainName}\{Environment.UserName}".ToUpperInvariant());
        return $"ADHDWindows.Browser.{Convert.ToHexString(SHA256.HashData(user))[..HashLength]}";
    }

    public static string HotkeyBusy(Guid session) => $@"Local\ADHDWindows.HotkeyBusy.{session:N}";
}
