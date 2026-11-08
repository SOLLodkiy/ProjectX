using System.Text.Json;
using System.Text.Json.Nodes;
using ADHDWindows.Core.Logging;
using Microsoft.Win32;

namespace ADHDWindows.Modules.Locks.Sites;

/// <summary>
/// Записи native messaging: по ним браузер находит мост для расширения ADHDWindows.
/// Записи лежат в разделе текущего пользователя и указывают на мост рядом с программой.
/// </summary>
public static class NativeHost
{
    public const string Name = "com.adhdwindows.bridge";

    /// <summary>Код расширения в Chromium. Он постоянный, потому что в манифесте задан открытый ключ.</summary>
    public const string ChromiumId = "diodcdmoopmnbkgblgffcbbhompfhpgg";

    public const string FirefoxId = "adhdwindows@adhdwindows.app";

    private static readonly string[] ChromiumKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Chromium\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\Yandex\YandexBrowser\NativeMessagingHosts",
    ];

    private const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts";

    public static string DefaultHostPath => Path.Combine(AppContext.BaseDirectory, "browser-host", "ADHDWindows.BrowserHost.exe");

    /// <summary>Записывает манифесты и записи реестра. Повтор ничего не меняет.</summary>
    public static bool Register(string hostPath, ILog log)
    {
        if (!File.Exists(hostPath))
        {
            log.Warn($"Нет моста браузера: {hostPath}");
            return false;
        }

        try
        {
            var folder = Path.GetDirectoryName(hostPath)!;
            var chromium = Path.Combine(folder, $"{Name}.chromium.json");
            var firefox = Path.Combine(folder, $"{Name}.firefox.json");
            WriteIfChanged(chromium, Manifest(hostPath, "allowed_origins", $"chrome-extension://{ChromiumId}/"));
            WriteIfChanged(firefox, Manifest(hostPath, "allowed_extensions", FirefoxId));

            foreach (var key in ChromiumKeys)
            {
                SetDefault($@"{key}\{Name}", chromium);
            }

            SetDefault($@"{FirefoxKey}\{Name}", firefox);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Error("Не удалось зарегистрировать мост браузера", exception);
            return false;
        }
    }

    private static string Manifest(string hostPath, string allowedName, string allowed) =>
        new JsonObject
        {
            ["name"] = Name,
            ["description"] = "ADHDWindows: связь расширения с программой",
            ["path"] = hostPath,
            ["type"] = "stdio",
            [allowedName] = new JsonArray(allowed),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static void WriteIfChanged(string path, string text)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != text)
        {
            File.WriteAllText(path, text);
        }
    }

    private static void SetDefault(string keyPath, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (key.GetValue(null) as string != value)
        {
            key.SetValue(null, value);
        }
    }
}
