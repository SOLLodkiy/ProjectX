using System.Diagnostics;
using System.Runtime.InteropServices;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Hardships;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Modules.Hardships.Cursor;
using ADHDWindows.Modules.Hardships.Physics;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

/// <summary>
/// Настоящая программа включает усложнения и завершается аварийно: курсор, скорость указателя и окна должны вернуться.
/// Курсор на несколько секунд меняется у всей системы, поэтому тесты идут вместе с тестами аварийной остановки, не параллельно.
/// </summary>
[Collection(nameof(EmergencyStopTests))]
public sealed class HardshipRecoveryTests : IDisposable
{
    private const string TestHotkey = "Ctrl+Alt+Shift+F9";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly List<Process> _started = [];
    private readonly int _speed = CursorControl.Speed();

    private string UserDir => _temp.Combine("user");

    private string Apps => _temp.Combine("apps");

    public void Dispose()
    {
        foreach (var process in _started)
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }

            process.Dispose();
        }

        // Если проверка упала посреди усложнения, человек не должен остаться без курсора.
        CursorControl.RestoreCursors();
        if (CursorControl.Speed() != _speed)
        {
            CursorControl.SetSpeed(_speed);
        }

        Thread.Sleep(200);
        _temp.Dispose();
    }

    [Fact]
    public void Killed_app_brings_back_the_invisible_cursor()
    {
        var visible = CursorControl.VisiblePixelsOfArrow();
        Assert.True(visible > 0);
        var app = StartApp("invisible");
        WaitUntil(() => AppLog().Contains("Усложнение включено: invisible"), "невидимый курсор");
        Assert.Equal(0, CursorControl.VisiblePixelsOfArrow());
        var watchdog = FindWatchdog();

        app.Kill();

        WaitUntil(() => CursorControl.VisiblePixelsOfArrow() == visible, "возврат курсора");
        WaitUntil(() => watchdog.HasExited, "завершение сторожа");
        Assert.Contains($"Откачено: {CursorRestorer.ChangeKind}", WatchdogLog());
    }

    [Fact]
    public void Killed_app_brings_back_the_clean_cursor_and_its_speed()
    {
        var visible = CursorControl.VisiblePixelsOfArrow();
        var app = StartApp("pollution");
        WaitUntil(() => AppLog().Contains("Усложнение включено: pollution"), "загрязнение");
        Assert.True(CursorControl.Speed() < _speed || _speed == 1);
        var watchdog = FindWatchdog();

        app.Kill();

        WaitUntil(() => CursorControl.Speed() == _speed && CursorControl.VisiblePixelsOfArrow() == visible, "возврат курсора и скорости");
        WaitUntil(() => watchdog.HasExited, "завершение сторожа");
    }

    [Fact]
    public void Killed_app_restores_windows_minimized_by_one_window()
    {
        var first = StartWindow();
        var second = StartWindow();
        var app = StartApp("oneWindow");
        WaitUntil(() => AppLog().Contains("Усложнение включено: oneWindow"), "одно окно");
        WaitUntil(() => WindowControl.IsMinimized(first) != WindowControl.IsMinimized(second), "сворачивание лишнего окна");
        var watchdog = FindWatchdog();

        app.Kill();

        WaitUntil(() => !WindowControl.IsMinimized(first) && !WindowControl.IsMinimized(second), "возврат окон");
        WaitUntil(() => watchdog.HasExited, "завершение сторожа");
        Assert.True(WindowControl.BelongsTo(first, _started[0].Id) && WindowControl.BelongsTo(second, _started[1].Id), "окна не должны закрываться");
    }

    [Fact]
    public void Killed_app_gives_the_mouse_buttons_back()
    {
        var swapped = MouseButtons.IsSwapped();
        try
        {
            var app = StartApp("swapButtons");
            WaitUntil(() => AppLog().Contains("Усложнение включено: swapButtons"), "смена кнопок");
            Assert.NotEqual(swapped, MouseButtons.IsSwapped());
            var watchdog = FindWatchdog();

            app.Kill();

            WaitUntil(() => MouseButtons.IsSwapped() == swapped, "возврат кнопок");
            WaitUntil(() => watchdog.HasExited, "завершение сторожа");
            Assert.Contains($"Откачено: {MouseButtonsRestorer.ChangeKind}", WatchdogLog());
        }
        finally
        {
            MouseButtons.SetSwapped(swapped);
        }
    }

    [Fact]
    public void Killed_app_puts_wandering_windows_back()
    {
        var window = StartWindow();
        WaitUntil(
            () =>
            {
                // Система разрешает сменить активное окно процессу, от которого пришёл последний ввод: F24 ничего не делает.
                SendInput(2, [Key(0x87, 0), Key(0x87, 0x0002)], Marshal.SizeOf<NativeInput>());
                WindowControl.TryActivate(window);
                return WindowControl.Foreground() == window;
            },
            "активное тестовое окно");
        var origin = WindowTraits.Bounds(window);
        var app = StartApp("wanderingWindows");
        WaitUntil(() => AppLog().Contains("Усложнение включено: wanderingWindows"), "бродячие окна");
        WaitUntil(() => WindowTraits.Bounds(window) != origin, "окно поползло");
        var watchdog = FindWatchdog();

        app.Kill();

        WaitUntil(() => WindowTraits.Bounds(window) == origin, "окно вернулось");
        WaitUntil(() => watchdog.HasExited, "завершение сторожа");
        Assert.Contains($"Откачено: {WindowPositionsRestorer.ChangeKind}", WatchdogLog());
    }

    [Fact]
    public void Killed_app_puts_windows_pushed_by_rocks_back()
    {
        // Окно во всю ширину монитора: каждый валун пролетает через его заголовок.
        var window = StartWindow();
        var area = TestScreen.Find()?.WorkArea ?? new ScreenRect(0, 0, 1280, 720);
        MoveWindow(window, area.Left, area.Top + 40, area.Width, 240, true);
        var origin = WindowTraits.Bounds(window);
        var app = StartApp("rockfall");
        WaitUntil(() => AppLog().Contains("Усложнение включено: rockfall"), "камнепад");
        WaitUntil(() => WindowTraits.Bounds(window).Top > origin.Top, "валун сдвинул окно");
        var watchdog = FindWatchdog();

        app.Kill();

        WaitUntil(() => WindowTraits.Bounds(window) == origin, "окно вернулось");
        WaitUntil(() => watchdog.HasExited, "завершение сторожа");
        Assert.Contains($"Откачено: {WindowPositionsRestorer.ChangeKind}", WatchdogLog());
    }

    [Fact]
    public void Emergency_hotkey_removes_the_hardship_and_quits()
    {
        var visible = CursorControl.VisiblePixelsOfArrow();
        var app = StartApp("invisible");
        WaitUntil(() => AppLog().Contains("Усложнение включено: invisible"), "невидимый курсор");
        var watchdog = FindWatchdog();
        WaitUntil(() => WatchdogLog().Contains("Горячая клавиша зарегистрирована"), "горячая клавиша");

        PressTestHotkey();

        WaitUntil(() => app.HasExited, "выход программы");
        WaitUntil(() => watchdog.HasExited, "завершение сторожа");
        Assert.Equal(visible, CursorControl.VisiblePixelsOfArrow());
        Assert.Contains("Усложнение снято: invisible", AppLog());
    }

    private IntPtr StartWindow()
    {
        if (!Directory.Exists(Apps))
        {
            var output = Path.Combine(RepositoryRoot.Find(), "tests", "ADHDWindows.TestApp", "bin", Configuration, "net10.0-windows");
            Directory.CreateDirectory(Apps);
            foreach (var file in Directory.GetFiles(output))
            {
                File.Copy(file, Path.Combine(Apps, Path.GetFileName(file)));
            }
        }

        var info = new ProcessStartInfo(Path.Combine(Apps, "ADHDWindows.TestApp.exe")) { UseShellExecute = false };
        info.ArgumentList.Add(_temp.Combine($"beat-{_started.Count}.txt"));
        var process = Process.Start(info)!;
        _started.Add(process);
        var window = IntPtr.Zero;
        WaitUntil(
            () =>
            {
                window = WindowControl.AppWindows().FirstOrDefault(item => item.ProcessId == process.Id)?.Handle ?? IntPtr.Zero;
                return window != IntPtr.Zero;
            },
            "окно тестовой программы");
        return window;
    }

    private Process StartApp(string hardship)
    {
        var paths = new AppPaths(_temp.Path, UserDir);
        var settings = new JsonFileStore(paths.SettingsPath, _log);
        settings.Load();
        settings.Set(Hotkey.SettingKey, TestHotkey);

        // Блокировка не трогает ни программы человека, ни тестовые окна.
        settings.Set("modules/locks/onlyUnder", _temp.Combine("locked"));
        settings.Set($"modules/hardships/{HardshipsModule.IgnoreSafetySetting}", true);
        settings.Set($"modules/hardship.oneWindow/{OneWindowHardship.OnlyUnderSetting}", Apps);
        settings.Set($"modules/hardship.wanderingWindows/{WanderingWindowsHardship.OnlyUnderSetting}", Apps);
        settings.Set($"modules/hardship.rockfall/{RockfallHardship.OnlyUnderSetting}", Apps);
        settings.Flush();

        var directory = Path.Combine(RepositoryRoot.Find(), "src", "ADHDWindows.App", "bin", Configuration, "net10.0-windows");
        var info = new ProcessStartInfo(Path.Combine(directory, "ADHDWindows.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        info.ArgumentList.Add(AppOptions.DeveloperFlag);
        info.ArgumentList.Add(AppOptions.UserDirPrefix + UserDir);
        info.ArgumentList.Add(AppOptions.HardshipPrefix + hardship);
        var process = Process.Start(info)!;
        _started.Add(process);
        return process;
    }

    private Process FindWatchdog()
    {
        var match = System.Text.RegularExpressions.Regex.Matches(WatchdogLog(), @"Сторож запущен, PID (\d+)");
        WaitUntil(
            () =>
            {
                match = System.Text.RegularExpressions.Regex.Matches(WatchdogLog(), @"Сторож запущен, PID (\d+)");
                return match.Count > 0;
            },
            "запуск сторожа");
        var process = Process.GetProcessById(int.Parse(match[^1].Groups[1].Value));
        _started.Add(process);
        return process;
    }

    private string AppLog() => ReadLogs(name => !name.StartsWith("watchdog-", StringComparison.Ordinal));

    private string WatchdogLog() => ReadLogs(name => name.StartsWith("watchdog-", StringComparison.Ordinal));

    private string ReadLogs(Func<string, bool> filter)
    {
        var logs = Path.Combine(UserDir, "logs");
        if (!Directory.Exists(logs))
        {
            return string.Empty;
        }

        var text = string.Empty;
        foreach (var file in Directory.GetFiles(logs, "*.log").Where(file => filter(Path.GetFileName(file))))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text += reader.ReadToEnd();
            }
            catch (IOException)
            {
                // Файл как раз пишется. Следующий опрос прочитает его.
            }
        }

        return text;
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Timeout, $"Не дождались: {what}");
            Thread.Sleep(50);
        }
    }

    private static void PressTestHotkey()
    {
        const ushort control = 0x11;
        const ushort alt = 0x12;
        const ushort shift = 0x10;
        const ushort f9 = 0x78;
        const uint keyUp = 0x0002;
        NativeInput[] inputs =
        [
            Key(control, 0),
            Key(alt, 0),
            Key(shift, 0),
            Key(f9, 0),
            Key(f9, keyUp),
            Key(shift, keyUp),
            Key(alt, keyUp),
            Key(control, keyUp),
        ];
        Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()));
    }

    private static NativeInput Key(ushort key, uint flags) => new() { Type = 1, VirtualKey = key, Flags = flags };

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput
    {
        [FieldOffset(0)]
        public int Type;

        [FieldOffset(8)]
        public ushort VirtualKey;

        [FieldOffset(12)]
        public uint Flags;
    }
}
