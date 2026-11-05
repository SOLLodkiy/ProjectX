using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

/// <summary>Настоящие процессы и настоящая клавиатура, поэтому параллельно с другими тестами не выполняются.</summary>
[CollectionDefinition(nameof(EmergencyStopTests), DisableParallelization = true)]
public sealed class EmergencyStopCollection;

[Collection(nameof(EmergencyStopTests))]
public sealed partial class EmergencyStopTests : IDisposable
{
    // Сочетание для тестов: обычная копия программы могла бы занять основное.
    private const string TestHotkey = "Ctrl+Alt+Shift+F9";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly string _token = $"test-{Guid.NewGuid():N}";
    private readonly List<Process> _started = [];

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private string UserDir => _temp.Combine("user");

    private string JournalPath => new AppPaths(_temp.Path, UserDir).RecoveryPath;

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

        // Сторож мог не успеть закрыться и держит файл журнала.
        Thread.Sleep(200);
        ProbeRegistry.Remove(_token);
        _temp.Dispose();
    }

    [Fact]
    public void Killed_app_is_rolled_back_by_the_watchdog()
    {
        var app = StartApp();
        WaitUntil(() => ProbeRegistry.Exists(_token), "тестовое изменение");
        var watchdog = FindWatchdog();

        app.Kill();

        WaitUntil(() => !ProbeRegistry.Exists(_token), "откат");
        WaitUntil(() => watchdog.HasExited, "завершение сторожа");
        Assert.Empty(new ChangeJournal(JournalPath, _log).Read());
    }

    [Fact]
    public void Change_is_rolled_back_on_next_start_when_the_watchdog_died_too()
    {
        var app = StartApp();
        WaitUntil(() => ProbeRegistry.Exists(_token), "тестовое изменение");
        var watchdog = FindWatchdog();

        // Сначала программа, иначе она перезапустит сторожа.
        app.Kill();
        watchdog.Kill();
        app.WaitForExit();
        watchdog.WaitForExit();

        // Сторож мог успеть откатить изменение. Тогда состояние сбоя воссоздаётся вручную.
        var token = $"test-{Guid.NewGuid():N}";
        ProbeRegistry.Apply(token);
        try
        {
            new ChangeJournal(JournalPath, _log).Add(new SystemChange(
                Guid.NewGuid(),
                "sample.probe",
                token,
                "sample",
                Guid.NewGuid(),
                DateTimeOffset.UtcNow));

            StartApp(probe: false);

            WaitUntil(() => !ProbeRegistry.Exists(token), "откат при запуске");
        }
        finally
        {
            ProbeRegistry.Remove(token);
        }
    }

    [Fact]
    public void Killed_watchdog_is_restarted()
    {
        StartApp();
        WaitUntil(() => ProbeRegistry.Exists(_token), "тестовое изменение");
        var first = FindWatchdog();

        first.Kill();

        WaitUntil(() => FindLatestWatchdogId() is { } id && id != first.Id, "новый сторож");
    }

    [Fact]
    public void Hotkey_stops_the_app_and_restores_the_system()
    {
        var app = StartApp();
        WaitUntil(() => ProbeRegistry.Exists(_token), "тестовое изменение");
        var watchdog = FindWatchdog();
        WaitForHotkey();

        PressTestHotkey();

        WaitUntil(() => app.HasExited, "выход программы");
        WaitUntil(() => watchdog.HasExited, "завершение сторожа");
        Assert.False(ProbeRegistry.Exists(_token));
        Assert.Equal(0, app.ExitCode);
    }

    [Fact]
    public void Hotkey_ends_a_hung_app_and_restores_the_system()
    {
        var app = StartApp();
        WaitUntil(() => ProbeRegistry.Exists(_token), "тестовое изменение");
        var watchdog = FindWatchdog();
        WaitForHotkey();
        Assert.Equal(0, NtSuspendProcess(app.Handle));

        PressTestHotkey();

        WaitUntil(() => app.HasExited, "принудительное завершение");
        WaitUntil(() => watchdog.HasExited, "завершение сторожа");
        Assert.False(ProbeRegistry.Exists(_token));
    }

    [Fact]
    public void Graceful_stop_leaves_nothing_for_the_watchdog()
    {
        // Клавиша закрывает программу тем же путём, что и пункт меню: модуль снимает изменение сам.
        var app = StartApp();
        WaitUntil(() => ProbeRegistry.Exists(_token), "тестовое изменение");
        var watchdog = FindWatchdog();
        WaitForHotkey();

        PressTestHotkey();
        WaitUntil(() => watchdog.HasExited, "завершение сторожа");

        Assert.DoesNotContain("Откат изменений", WatchdogLog());
        Assert.True(app.HasExited);
    }

    [Fact]
    public void Real_hotkey_fires_and_a_taken_one_is_reported()
    {
        Assert.True(Hotkey.TryParse("Ctrl+Alt+Shift+F10", out var hotkey));
        using var pressed = new ManualResetEventSlim();
        using var first = new GlobalHotkey(hotkey, pressed.Set, _log);
        Assert.True(first.Start());

        using (var second = new GlobalHotkey(hotkey, () => { }, _log))
        {
            Assert.False(second.Start());
        }

        Press(0x79);

        Assert.True(pressed.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Hotkey_is_released_on_dispose()
    {
        Assert.True(Hotkey.TryParse("Ctrl+Alt+Shift+F11", out var hotkey));
        var first = new GlobalHotkey(hotkey, () => { }, _log);
        Assert.True(first.Start());
        first.Dispose();
        first.Dispose();

        using var second = new GlobalHotkey(hotkey, () => { }, _log);

        Assert.True(second.Start());
    }

    private Process StartApp(bool probe = true)
    {
        var settings = new JsonFileStore(new AppPaths(_temp.Path, UserDir).SettingsPath, _log);
        settings.Load();
        settings.Set(Hotkey.SettingKey, TestHotkey);
        settings.Set("modules/locks/onlyUnder", _temp.Path);
        settings.Flush();

        var directory = Path.Combine(RepositoryRoot.Find(), "src", "ADHDWindows.App", "bin", Configuration, "net10.0-windows");
        var info = new ProcessStartInfo(Path.Combine(directory, "ADHDWindows.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        info.ArgumentList.Add(AppOptions.DeveloperFlag);
        info.ArgumentList.Add(AppOptions.UserDirPrefix + UserDir);
        if (probe)
        {
            info.ArgumentList.Add(AppOptions.RecoveryProbePrefix + _token);
        }

        var process = Process.Start(info)!;
        _started.Add(process);
        return process;
    }

    private Process FindWatchdog()
    {
        WaitUntil(() => FindLatestWatchdogId() is not null, "запуск сторожа");
        var process = Process.GetProcessById(FindLatestWatchdogId()!.Value);
        _started.Add(process);
        return process;
    }

    private int? FindLatestWatchdogId()
    {
        var matches = WatchdogPid().Matches(WatchdogLog());
        return matches.Count == 0 ? null : int.Parse(matches[^1].Groups[1].Value);
    }

    private void WaitForHotkey() =>
        WaitUntil(() => WatchdogLog().Contains("Горячая клавиша зарегистрирована"), "горячая клавиша");

    private string WatchdogLog()
    {
        var logs = Path.Combine(UserDir, "logs");
        if (!Directory.Exists(logs))
        {
            return string.Empty;
        }

        var text = string.Empty;
        foreach (var file in Directory.GetFiles(logs, "watchdog-*.log"))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text += reader.ReadToEnd();
            }
            catch (IOException)
            {
                // Сторож как раз пишет в файл. Следующий опрос прочитает его.
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

    private static void PressTestHotkey() => Press(0x78);

    private static void Press(ushort key)
    {
        const ushort control = 0x11;
        const ushort alt = 0x12;
        const ushort shift = 0x10;
        const uint keyUp = 0x0002;
        NativeInput[] inputs =
        [
            Key(control, 0),
            Key(alt, 0),
            Key(shift, 0),
            Key(key, 0),
            Key(key, keyUp),
            Key(shift, keyUp),
            Key(alt, keyUp),
            Key(control, keyUp),
        ];
        Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()));
    }

    private static NativeInput Key(ushort key, uint flags) => new() { Type = 1, VirtualKey = key, Flags = flags };

    [GeneratedRegex(@"Сторож запущен, PID (\d+)")]
    private static partial Regex WatchdogPid();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);

    // Размер как у INPUT в 64-битном процессе: объединение задаётся MOUSEINPUT.
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
