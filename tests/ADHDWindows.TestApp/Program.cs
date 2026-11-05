using System.Windows.Forms;

namespace ADHDWindows.TestApp;

/// <summary>
/// Окно со счётчиком в памяти. Каждый запуск пишет в файл из аргумента свой номер и значение счётчика.
/// Если значение перестало расти, программа заморожена. Если оно продолжается с того же места, память не потеряна.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var path = args[0];
        var counter = 0L;
        using var form = new Form { Text = "Lock Fixture", Width = 320, Height = 200 };
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
}
