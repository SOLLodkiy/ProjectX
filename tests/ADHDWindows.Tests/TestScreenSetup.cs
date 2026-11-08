using System.Globalization;
using System.Runtime.CompilerServices;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

/// <summary>
/// Окна тестов открываются на втором мониторе, чтобы не мешать человеку на основном.
/// Переменная наследуется тестовой программой и запущенным ADHDWindows. Если монитор один, ничего не меняется.
/// Заранее заданная переменная не переписывается: так можно выбрать другой монитор.
/// </summary>
internal static class TestScreenSetup
{
    [ModuleInitializer]
    internal static void Choose()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestScreen.Variable)))
        {
            return;
        }

        if (Desktop.Screens().FirstOrDefault(screen => !screen.IsPrimary) is { } second)
        {
            var x = second.Bounds.Left + (second.Bounds.Width / 2);
            var y = second.Bounds.Top + (second.Bounds.Height / 2);
            Environment.SetEnvironmentVariable(TestScreen.Variable, string.Create(CultureInfo.InvariantCulture, $"{x},{y}"));
        }
    }
}
