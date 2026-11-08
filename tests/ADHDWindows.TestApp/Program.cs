using System.Globalization;
using System.Windows.Forms;

namespace ADHDWindows.TestApp;

/// <summary>
/// Окно со счётчиком в памяти. Каждый запуск пишет в файл из аргумента свой номер и значение счётчика.
/// Если значение перестало расти, программа заморожена. Если оно продолжается с того же места, память не потеряна.
/// </summary>
internal static class Program
{
    // То же имя, что у ADHDWindows.Platform.TestScreen: тесты открывают окна на втором мониторе.
    private const string TestScreenVariable = "ADHDWINDOWS_TEST_SCREEN";
    private const int Margin = 48;

    [STAThread]
    private static void Main(string[] args)
    {
        // Координаты в пикселях экрана, как в переменной.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var path = args[0];
        var counter = 0L;
        using var form = new Form { Text = "Lock Fixture", Width = 320, Height = 200 };
        if (TestScreenArea() is { } area)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(area.Right - form.Width - Margin, area.Top + Margin);
        }

        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += (_, _) =>
        {
            counter++;
            try
            {
                File.WriteAllText(path, $"{Environment.ProcessId} {counter}");
            }
            catch (IOException)
            {
                // Тест как раз читает файл.
            }
        };
        timer.Start();
        Application.Run(form);
    }

    private static System.Drawing.Rectangle? TestScreenArea()
    {
        var parts = Environment.GetEnvironmentVariable(TestScreenVariable)?.Split(',');
        if (parts is not { Length: 2 }
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            return null;
        }

        var screen = Screen.AllScreens.FirstOrDefault(item => item.Bounds.Contains(x, y));
        return screen?.WorkingArea;
    }
}
