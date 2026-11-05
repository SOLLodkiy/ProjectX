using ADHDWindows.Core.Safety;
using Microsoft.Win32;

namespace ADHDWindows.Modules.Sample;

/// <summary>
/// Безопасное тестовое изменение системы: значение в собственном разделе реестра Windows.
/// Как и настоящие изменения, оно не исчезает само при завершении процесса.
/// </summary>
public sealed class SampleProbe : ISystemRestorer
{
    public const string KeyPath = @"Software\ADHDWindowsRecoveryProbe";
    public const string ChangeKind = "sample.probe";

    public string Kind => ChangeKind;

    public static void Apply(string token)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(token, 1);
        }
    }

    public void Restore(string data)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true))
        {
            if (key is null)
            {
                return;
            }

            key.DeleteValue(data, throwOnMissingValue: false);
            if (key.ValueCount > 0)
            {
                return;
            }
        }

        Registry.CurrentUser.DeleteSubKey(KeyPath, throwOnMissingSubKey: false);
    }
}
