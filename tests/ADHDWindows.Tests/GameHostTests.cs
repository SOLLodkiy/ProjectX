using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hosting;

namespace ADHDWindows.Tests;

public sealed class GameHostTests : IDisposable
{
    private const string SampleRunsKey = "modules/sample/runs";

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly AppPaths _paths;

    public GameHostTests() => _paths = new AppPaths(_temp.Combine("install"), _temp.Combine("user"));

    public void Dispose() => _temp.Dispose();

    private GameHost CreateHost() => new(_paths, new AppOptions(DeveloperMode: false), _log);

    [Fact]
    public void Sample_module_state_survives_a_restart()
    {
        RepositoryRoot.DeploySampleModule(_paths.ModulesDir);

        using (var first = CreateHost())
        {
            first.Start();
            Assert.Equal("sample", Assert.Single(first.RunningModules).Id);
        }

        using var second = CreateHost();
        second.Start();

        Assert.Equal(2, second.Saves.Get<int>(SampleRunsKey));
        Assert.Contains(_log.Infos, line => line == "[sample] Запуск номер 2");
    }

    [Fact]
    public void Settings_survive_a_restart()
    {
        using (var first = CreateHost())
        {
            first.Start();
            first.Settings.Set("volume", 30);
        }

        using var second = CreateHost();
        second.Start();

        Assert.Equal(30, second.Settings.Get<int>("volume"));
    }

    [Fact]
    public void Host_starts_without_modules_and_data()
    {
        using var host = CreateHost();

        host.Start();

        Assert.Empty(host.RunningModules);
    }

    [Fact]
    public void Language_comes_from_settings()
    {
        Directory.CreateDirectory(Path.Combine(_paths.DataDir, "texts"));
        File.WriteAllText(Path.Combine(_paths.DataDir, "texts", "en.json"), """{ "tray.exit": "Exit" }""");
        using (var first = CreateHost())
        {
            first.Start();
            first.Settings.Set(GameHost.LanguageSettingKey, "en");
        }

        using var second = CreateHost();
        second.Start();

        Assert.Equal("Exit", second.Texts.Get("tray.exit"));
    }

    [Fact]
    public void Lifecycle_events_are_published_once()
    {
        var stops = 0;
        var host = CreateHost();
        host.Events.Subscribe<AppStopping>(_ => stops++);

        host.Start();
        host.Stop();
        host.Stop();
        host.Dispose();

        Assert.Equal(1, stops);
    }

    [Fact]
    public void Save_failure_is_logged_and_does_not_throw()
    {
        using var host = CreateHost();
        host.Start();
        host.Settings.Set("volume", 1);
        host.Save();
        host.Settings.Set("volume", 2);

        using (new FileStream(_paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            host.Save();
        }

        Assert.Single(_log.Errors);
    }

    [Fact]
    public void Developer_flag_is_parsed()
    {
        Assert.True(AppOptions.Parse(["--dev"]).DeveloperMode);
        Assert.False(AppOptions.Parse(["--other"]).DeveloperMode);
        Assert.False(AppOptions.Parse([]).DeveloperMode);
    }
}
