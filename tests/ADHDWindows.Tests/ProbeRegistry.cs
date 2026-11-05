using Microsoft.Win32;

namespace ADHDWindows.Tests;

/// <summary>Раздел реестра Windows с тестовыми изменениями модуля Sample.</summary>
internal static class ProbeRegistry
{
    private const string KeyPath = @"Software\ADHDWindowsRecoveryProbe";

    public static void Apply(string token)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(token, 1);
    }

    public static bool Exists(string token)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(token) is not null;
    }

    public static void Remove(string token)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true))
        {
            if (key is null)
            {
                return;
            }

            key.DeleteValue(token, throwOnMissingValue: false);
            if (key.ValueCount > 0)
            {
                return;
            }
        }

        Registry.CurrentUser.DeleteSubKey(KeyPath, throwOnMissingSubKey: false);
    }
}
