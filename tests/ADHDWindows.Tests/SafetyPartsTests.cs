using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

public sealed class SafetyPartsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("Ctrl+Alt+Shift+Q", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'Q')]
    [InlineData("ctrl + q", HotkeyModifiers.Control, 'Q')]
    [InlineData("Win+Shift+7", HotkeyModifiers.Win | HotkeyModifiers.Shift, '7')]
    [InlineData("Alt+F12", HotkeyModifiers.Alt, 0x7B)]
    [InlineData("Control+F1", HotkeyModifiers.Control, 0x70)]
    public void Hotkey_is_parsed(string text, HotkeyModifiers modifiers, int key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Q")]
    [InlineData("F5")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+Q")]
    [InlineData("Ctrl+Я")]
    [InlineData("Ctrl+F25")]
    [InlineData("Hyper+Q")]
    public void Unusable_hotkey_is_refused(string? text) => Assert.False(Hotkey.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Alt+Shift+Q")]
    [InlineData("Ctrl+Win+F24")]
    public void Hotkey_text_survives_a_round_trip(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(text, hotkey.ToString());
    }

    [Fact]
    public void Invalid_hotkey_setting_falls_back_to_the_default()
    {
        var store = new JsonFileStore(_temp.Combine("settings.json"), _log);
        store.Set(Hotkey.SettingKey, "абракадабра");

        Assert.Equal(Hotkey.Default, Hotkey.FromSettings(store, _log));
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public void Hotkey_setting_is_used()
    {
        var store = new JsonFileStore(_temp.Combine("settings.json"), _log);
        store.Set(Hotkey.SettingKey, "Ctrl+F2");

        Assert.Equal("Ctrl+F2", Hotkey.FromSettings(store, _log).ToString());
    }

    [Fact]
    public void Watchdog_arguments_survive_a_round_trip()
    {
        var arguments = new WatchdogArguments(42, 133_000_000_000_000_000, Guid.NewGuid(), @"D:\Дума\Папка с пробелами\", @"C:\user", "Ctrl+Q");

        Assert.Equal(arguments, WatchdogArguments.Parse(arguments.ToList()));
    }

    [Theory]
    [InlineData]
    [InlineData("1", "2", "3", "4", "5", "6")]
    public void Invalid_watchdog_arguments_are_refused(params string[] args) =>
        Assert.Null(WatchdogArguments.Parse(args));

    [Fact]
    public void Test_options_work_only_in_developer_mode()
    {
        var normal = AppOptions.Parse([@"--user-dir=C:\other", "--recovery-probe=x"]);
        var developer = AppOptions.Parse(["--dev", @"--user-dir=C:\other", "--recovery-probe=x"]);

        Assert.Null(normal.UserDir);
        Assert.Null(normal.RecoveryProbe);
        Assert.Equal(@"C:\other", developer.UserDir);
        Assert.Equal("x", developer.RecoveryProbe);
    }

    [Fact]
    public void Default_instance_name_stays_and_a_test_folder_gets_its_own()
    {
        Assert.Equal(SingleInstanceGuard.ApplicationName, SingleInstanceGuard.NameFor(null));
        Assert.NotEqual(SingleInstanceGuard.ApplicationName, SingleInstanceGuard.NameFor(@"C:\other"));
        Assert.Equal(SingleInstanceGuard.NameFor(@"C:\Other"), SingleInstanceGuard.NameFor(@"c:\other"));
    }

    [Fact]
    public void Signal_reaches_the_listener()
    {
        var name = $@"Local\ADHDWindows.Tests.{Guid.NewGuid():N}";
        using var raised = new ManualResetEventSlim();
        using var signal = new NamedSignal(name, raised.Set);

        Assert.True(NamedSignal.Raise(name));

        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Signal_without_a_listener_is_reported() =>
        Assert.False(NamedSignal.Raise($@"Local\ADHDWindows.Tests.{Guid.NewGuid():N}"));

    [Fact]
    public void Missing_watchdog_is_reported_and_does_not_throw()
    {
        var arguments = new WatchdogArguments(1, 1, Guid.NewGuid(), _temp.Path, _temp.Path, "Ctrl+Q");
        using var watchdog = new WatchdogProcess(_temp.Combine("нет.exe"), arguments, _log);

        Assert.False(watchdog.Start());
        Assert.Single(_log.Errors);
    }
}
