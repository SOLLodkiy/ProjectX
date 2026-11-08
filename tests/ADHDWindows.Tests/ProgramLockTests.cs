using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Locks;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

/// <summary>
/// Настоящая программа замораживает настоящий процесс. Блокировка ограничена папкой теста:
/// без этого она заморозила бы программы, открытые у пользователя.
/// </summary>
[Collection(nameof(EmergencyStopTests))]
public sealed class ProgramLockTests : IDisposable
{
    private const string TestHotkey = "Ctrl+Alt+Shift+F8";
    private const string BuyButton = "Разблокировать";
    private const long Balance = 1000;
    private const long DefaultPrice = 300;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(700);

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly List<Process> _started = [];

    private string UserDir => _temp.Combine("user");

    private AppPaths Paths => new(_temp.Path, UserDir);

    private string Scope => _temp.Combine("programs");

    private string Beat => _temp.Combine("beat.txt");

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

        Thread.Sleep(200);
        _temp.Dispose();
    }

    [Fact]
    public void Already_open_program_freezes_and_keeps_memory_after_purchase()
    {
        var program = StartProgram();
        var before = WaitForBeat();
        var app = StartApp();

        var frozen = WaitUntilFrozen();
        Assert.True(IsMinimized(program));
        Assert.Contains(new ChangeJournal(Paths.RecoveryPath, _log).Read(), change => change.Kind == FreezeRestorer.ChangeKind);

        Invoke(WaitForBuyButton(app));

        WaitUntil(() => ReadBeat() > frozen, "разморозка");
        Assert.True(ReadBeat() > before);
        Assert.Equal(program.Id, ReadBeatOwner());
        WaitUntil(() => !IsMinimized(program), "разворачивание окна");
        WaitUntil(() => new ChangeJournal(Paths.RecoveryPath, _log).Read().Count == 0, "очистка реестра изменений");

        Exit(app);

        // Настоящая мышь тоже приносит монетки, пока идёт тест.
        Assert.InRange(ReadSave().Get<long>(CoinEconomy.BalanceKey), Balance - DefaultPrice, Balance - 1);

        // Покупка действует и после перезапуска.
        var second = StartApp(seed: false);
        WaitForLog("Модуль запущен: locks");
        Thread.Sleep(Quiet);
        var alive = ReadBeat();
        Thread.Sleep(Quiet);
        Assert.True(ReadBeat() > alive);
        Exit(second);
    }

    [Fact]
    public void Program_started_later_is_frozen_too()
    {
        StartApp();
        WaitForLog("Модуль запущен: locks");

        StartProgram();

        WaitUntilFrozen();
    }

    [Fact]
    public void Renamed_copy_is_the_same_program()
    {
        var original = StartProgram();
        var app = StartApp();
        WaitUntilFrozen();
        Invoke(WaitForBuyButton(app));
        WaitUntil(() => !IsMinimized(original), "покупка");
        original.Kill();
        original.WaitForExit();
        Exit(app);

        // Копия под другим именем уже куплена.
        File.Copy(ProgramPath, Path.Combine(Scope, "totally-other.exe"));
        StartApp(seed: false);
        WaitForLog("Модуль запущен: locks");
        File.Delete(Beat);
        StartProgram("totally-other.exe");
        WaitForBeat();
        Thread.Sleep(TimeSpan.FromSeconds(4));
        var alive = ReadBeat();
        Thread.Sleep(Quiet);
        Assert.True(ReadBeat() > alive);
    }

    [Fact]
    public void Renaming_an_unpaid_program_does_not_avoid_the_lock()
    {
        DeployProgram();
        File.Copy(ProgramPath, Path.Combine(Scope, "claude.exe"));
        StartApp();
        WaitForLog("Модуль запущен: locks");

        StartProgram("claude.exe");

        WaitUntilFrozen();
    }

    [Fact]
    public void Emergency_stop_thaws_everything_and_restores_the_window()
    {
        var program = StartProgram();
        var app = StartApp();
        var frozen = WaitUntilFrozen();
        WaitForWatchdogHotkey();

        PressHotkey();

        WaitUntil(() => app.HasExited, "выход программы");
        WaitUntil(() => ReadBeat() > frozen, "разморозка");
        WaitUntil(() => !IsMinimized(program), "разворачивание окна");
    }

    [Fact]
    public void Killed_app_is_thawed_by_the_watchdog()
    {
        var program = StartProgram();
        var app = StartApp();
        var frozen = WaitUntilFrozen();
        WaitForWatchdogLog("Сторож запущен");

        app.Kill();

        WaitUntil(() => ReadBeat() > frozen, "разморозка сторожем");
        WaitUntil(() => !IsMinimized(program), "разворачивание окна");
        WaitUntil(() => new ChangeJournal(Paths.RecoveryPath, _log).Read().Count == 0, "очистка реестра изменений");
    }

    [Fact]
    public void Starting_the_program_again_reopens_the_closed_screen()
    {
        StartProgram();
        var app = StartApp();
        WaitUntilFrozen();
        WaitForBuyButton(app);
        var later = FindByName(app, "Позже");
        Assert.NotNull(later);
        Invoke(later);
        WaitUntil(() => FindBuyButton(app) is null, "закрытие экрана");

        var copy = StartProgram();

        WaitForBuyButton(app);
        WaitUntil(() => IsMinimized(copy), "вторая копия свёрнута");
    }

    [Fact]
    public void Exit_thaws_but_the_lock_returns_on_next_start()
    {
        StartProgram();
        var app = StartApp();
        var frozen = WaitUntilFrozen();

        Exit(app);
        WaitUntil(() => ReadBeat() > frozen, "разморозка при выходе");

        StartApp(seed: false);
        WaitUntilFrozen();
    }

    [Fact]
    public void Without_coins_the_buy_button_is_disabled()
    {
        StartProgram();
        var app = StartApp(balance: 10);
        WaitUntilFrozen();

        var button = WaitForBuyButton(app);

        Assert.False(button.Current.IsEnabled);
    }

    [Fact]
    public void Restorer_ignores_a_process_with_a_reused_number_and_repeats_safely()
    {
        var program = StartProgram();
        var frozen = WaitForBeat();
        var stamp = ProcessControl.Stamp(program.Id)!.Value;
        Assert.True(ProcessControl.Freeze(stamp));
        Thread.Sleep(Quiet);
        var held = ReadBeat();
        var restorer = new FreezeRestorer();

        restorer.Restore(new FreezeRecord([stamp with { StartTime = stamp.StartTime + 1 }], []).ToData());
        Thread.Sleep(Quiet);
        Assert.Equal(held, ReadBeat());

        var data = new FreezeRecord([stamp], []).ToData();
        restorer.Restore(data);
        restorer.Restore(data);
        WaitUntil(() => ReadBeat() > held, "разморозка");
        Assert.True(held >= frozen);
    }

    [Fact]
    public void Window_watcher_sees_a_new_window()
    {
        using var seen = new ManualResetEventSlim();
        Process? program = null;
        using var watcher = new WindowWatcher(
            (window, _) =>
            {
                if (program is not null && WindowControl.BelongsTo(window, program.Id))
                {
                    seen.Set();
                }
            },
            _log);
        Assert.True(watcher.Start());

        program = StartProgram();

        Assert.True(seen.Wait(Timeout));
    }

    [Fact]
    public void Game_installed_while_running_and_started_directly_is_locked_as_a_game()
    {
        // Steam установлен, но игры в нём ещё нет.
        DeployProgram();
        Directory.CreateDirectory(Path.Combine(SteamFolder, "steamapps", "common"));
        StartApp();
        WaitForLog("Найдено игр:");

        // Установка появляется, когда программа уже работает, и игра запускается раньше планового перечитывания.
        DeployGame();
        StartGame(throughLauncher: false);

        WaitUntilFrozen();
        WaitForLog($"Заблокировано: {GameName}, цена 1500");
        WaitForLog($"Новая игра: {GameName} (steam), цена 1500");
    }

    [Fact]
    public void Game_started_through_a_launcher_is_locked_and_bought_separately()
    {
        DeployGame();
        var app = StartApp(balance: 2000);
        WaitForLog("Найдено игр:");

        StartGame(throughLauncher: true);
        var frozen = WaitUntilFrozen();
        WaitForLog($"Заблокировано: {GameName}, цена 1500");

        Invoke(WaitForBuyButton(app));
        WaitUntil(() => ReadBeat() > frozen, "разморозка после покупки");
        Exit(app);

        var unlocked = ReadSave().Get<Dictionary<string, UnlockRecord>>($"modules/locks/{UnlockLedger.SaveKey}")!;
        var record = unlocked[$"game:steam:{GameId}"];
        Assert.Equal(Core.Locks.LockKind.Game, record.Kind);
        Assert.Equal(1500, record.Price);
    }

    private const string GameId = "4242";
    private const string GameName = "Fake Game";

    private string SteamFolder => Path.Combine(Scope, "steam");

    private string GameFolder => Path.Combine(SteamFolder, "steamapps", "common", GameName);

    /// <summary>Тестовая программа под видом игры в библиотеке Steam внутри папки теста.</summary>
    private void DeployGame()
    {
        DeployProgram();
        Directory.CreateDirectory(GameFolder);
        foreach (var file in Directory.GetFiles(Scope))
        {
            File.Copy(file, Path.Combine(GameFolder, Path.GetFileName(file)));
        }

        GameLibraryTests.WriteSteamGame(SteamFolder, GameId, GameName, GameName, 1024 * 1024);
    }

    /// <param name="throughLauncher">Запустить через другой процесс, как это делает лаунчер.</param>
    private void StartGame(bool throughLauncher)
    {
        var game = Path.Combine(GameFolder, "ADHDWindows.TestApp.exe");
        ProcessStartInfo info;
        if (throughLauncher)
        {
            info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c start \"\" \"{game}\" \"{Beat}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }
        else
        {
            info = new ProcessStartInfo(game) { UseShellExecute = false };
            info.ArgumentList.Add(Beat);
        }

        _started.Add(Process.Start(info)!);
        if (throughLauncher)
        {
            // Сама игра — не дочерний процесс теста, её нужно найти, чтобы закрыть в конце.
            WaitUntil(() => FindGameProcess() is not null, "запуск игры через лаунчер");
            _started.Add(FindGameProcess()!);
        }
    }

    private Process? FindGameProcess() =>
        Process.GetProcessesByName("ADHDWindows.TestApp")
            .FirstOrDefault(process =>
            {
                try
                {
                    return process.MainModule?.FileName.StartsWith(GameFolder, StringComparison.OrdinalIgnoreCase) == true;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return false;
                }
            });

    private string ProgramPath => Path.Combine(Scope, "ADHDWindows.TestApp.exe");

    private Process StartProgram(string? name = null)
    {
        DeployProgram();
        var info = new ProcessStartInfo(Path.Combine(Scope, name ?? "ADHDWindows.TestApp.exe")) { UseShellExecute = false };
        info.ArgumentList.Add(Beat);
        var process = Process.Start(info)!;
        _started.Add(process);
        return process;
    }

    private void DeployProgram()
    {
        if (Directory.Exists(Scope))
        {
            return;
        }

        var output = Path.Combine(RepositoryRoot.Find(), "tests", "ADHDWindows.TestApp", "bin", Configuration, "net10.0-windows");
        Directory.CreateDirectory(Scope);
        foreach (var file in Directory.GetFiles(output))
        {
            File.Copy(file, Path.Combine(Scope, Path.GetFileName(file)));
        }
    }

    private Process StartApp(long balance = Balance, bool seed = true)
    {
        DeployProgram();
        if (seed)
        {
            var saves = new JsonFileStore(Paths.SavePath, _log);
            saves.Load();
            saves.Set(CoinEconomy.BalanceKey, balance);
            saves.Flush();
        }

        var settings = new JsonFileStore(Paths.SettingsPath, _log);
        settings.Load();
        settings.Set(Hotkey.SettingKey, TestHotkey);
        settings.Set($"modules/locks/{LocksModule.OnlyUnderSetting}", Scope);
        settings.Set($"modules/locks/{LocksModule.SteamPathSetting}", SteamFolder);
        settings.Set($"modules/locks/{LocksModule.EpicManifestsSetting}", Path.Combine(Scope, "epic"));
        settings.Flush();

        var directory = Path.Combine(RepositoryRoot.Find(), "src", "ADHDWindows.App", "bin", Configuration, "net10.0-windows");
        var info = new ProcessStartInfo(Path.Combine(directory, "ADHDWindows.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        info.ArgumentList.Add(AppOptions.DeveloperFlag);
        info.ArgumentList.Add(AppOptions.UserDirPrefix + UserDir);
        var process = Process.Start(info)!;
        _started.Add(process);
        return process;
    }

    private JsonFileStore ReadSave()
    {
        var saves = new JsonFileStore(Paths.SavePath, _log);
        saves.Load();
        return saves;
    }

    private void Exit(Process app)
    {
        WaitForWatchdogHotkey();
        PressHotkey();
        WaitUntil(() => app.HasExited, "выход программы");

        // Сторож держит блокировку реестра изменений, пока завершается.
        Thread.Sleep(300);
    }

    private long WaitForBeat()
    {
        WaitUntil(() => ReadBeat() > 0, "работа тестовой программы");
        return ReadBeat();
    }

    /// <summary>Возвращает значение счётчика, на котором программа остановилась.</summary>
    private long WaitUntilFrozen()
    {
        var watch = Stopwatch.StartNew();
        var last = ReadBeat();
        while (true)
        {
            Assert.True(watch.Elapsed < Timeout, "Программа не заморозилась");
            Thread.Sleep(Quiet);
            var current = ReadBeat();
            if (current == last && current > 0)
            {
                return current;
            }

            last = current;
        }
    }

    private long ReadBeat() => ReadBeatParts()?.Counter ?? 0;

    private int ReadBeatOwner() => ReadBeatParts()?.Owner ?? 0;

    private (int Owner, long Counter)? ReadBeatParts()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (!File.Exists(Beat))
                {
                    return null;
                }

                var parts = File.ReadAllText(Beat).Split(' ');
                if (parts.Length == 2
                    && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var owner)
                    && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var counter))
                {
                    return (owner, counter);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Программа как раз пишет в файл.
            }

            Thread.Sleep(10);
        }

        return null;
    }

    private static bool IsMinimized(Process program)
    {
        program.Refresh();
        return WindowControl.AppWindows()
            .Where(window => window.ProcessId == program.Id)
            .All(window => window.IsMinimized);
    }

    private static AutomationElement WaitForBuyButton(Process app)
    {
        AutomationElement? button = null;
        WaitUntil(() => (button = FindBuyButton(app)) is not null, "кнопка покупки");
        return button!;
    }

    private static AutomationElement? FindBuyButton(Process app) => FindByName(app, BuyButton);

    /// <summary>
    /// Окна программы перебираются через Win32, а не через корень UI Automation: тот опрашивает все окна верхнего уровня,
    /// включая окна замороженных процессов, и каждый такой опрос ждёт таймаута. Один поиск тогда длился дольше
    /// всего ожидания, и тест падал, хотя экран покупки уже был открыт.
    /// </summary>
    private static AutomationElement? FindByName(Process app, string name)
    {
        foreach (var handle in VisibleWindowsOf(app.Id))
        {
            AutomationElement window;
            try
            {
                window = AutomationElement.FromHandle(handle);
            }
            catch (ElementNotAvailableException)
            {
                // Окно закрылось между перебором и запросом.
                continue;
            }

            if (window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)) is { } button)
            {
                return button;
            }
        }

        return null;
    }

    private static List<IntPtr> VisibleWindowsOf(int processId)
    {
        var windows = new List<IntPtr>();
        EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner == processId && IsWindowVisible(window))
                {
                    windows.Add(window);
                }

                return true;
            },
            IntPtr.Zero);
        return windows;
    }

    private static void Invoke(AutomationElement button) =>
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private void WaitForLog(string text) => WaitUntil(() => ReadLogs("*.log").Contains(text), text);

    private void WaitForWatchdogLog(string text) => WaitUntil(() => ReadLogs("watchdog-*.log").Contains(text), text);

    private void WaitForWatchdogHotkey() => WaitUntil(() => HotkeyIsRegistered(), "горячая клавиша");

    /// <summary>Сторож пишет в один файл при каждом запуске программы, поэтому важна последняя его запись.</summary>
    private bool HotkeyIsRegistered()
    {
        var log = ReadLogs("watchdog-*.log");
        var lastStart = log.LastIndexOf("Сторож запущен", StringComparison.Ordinal);
        return lastStart >= 0 && log.IndexOf("Горячая клавиша зарегистрирована", lastStart, StringComparison.Ordinal) >= 0;
    }

    private string ReadLogs(params string[] patterns)
    {
        var logs = Path.Combine(UserDir, "logs");
        if (!Directory.Exists(logs))
        {
            return string.Empty;
        }

        var text = string.Empty;
        foreach (var file in patterns.SelectMany(pattern => Directory.GetFiles(logs, pattern)).Distinct())
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

    private static void PressHotkey()
    {
        const ushort control = 0x11;
        const ushort alt = 0x12;
        const ushort shift = 0x10;
        const ushort f8 = 0x77;
        const uint keyUp = 0x0002;
        NativeInput[] inputs =
        [
            Key(control, 0),
            Key(alt, 0),
            Key(shift, 0),
            Key(f8, 0),
            Key(f8, keyUp),
            Key(shift, keyUp),
            Key(alt, keyUp),
            Key(control, keyUp),
        ];
        Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()));
    }

    private static NativeInput Key(ushort key, uint flags) => new() { Type = 1, VirtualKey = key, Flags = flags };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

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
