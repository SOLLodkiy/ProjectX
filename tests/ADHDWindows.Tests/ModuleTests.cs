using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Tests;

public sealed class ModuleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private ModuleHost CreateHost(EventBus bus)
    {
        var data = new GameData(_temp.Path, _log);
        var store = new JsonFileStore(_temp.Combine("store.json"), _log);
        var economy = new CoinEconomy(store, bus, TimeProvider.System);
                return new ModuleHost(bus, data, new TextCatalog(data, "ru", _log), economy, store, store, new RecoveryService(new ChangeJournal(_temp.Combine("recovery.json"), _log), () => [], _log), _log, developerMode: false);
    }

    [Fact]
    public void Module_from_folder_is_loaded_without_a_reference_from_the_host()
    {
        var modules = _temp.Combine("modules");
        RepositoryRoot.DeploySampleModule(modules);

        var loaded = ModuleLoader.Load(modules, _log);

        var module = Assert.Single(loaded);
        Assert.Equal("sample", module.Id);
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Broken_assembly_is_reported_and_other_modules_still_load()
    {
        var modules = _temp.Combine("modules");
        RepositoryRoot.DeploySampleModule(modules);
        Directory.CreateDirectory(Path.Combine(modules, "Broken"));
        File.WriteAllText(Path.Combine(modules, "Broken", "Broken.dll"), "не сборка");

        var loaded = ModuleLoader.Load(modules, _log);

        Assert.Single(loaded);
        Assert.Single(_log.Errors);
    }

    [Fact]
    public void Folder_without_module_assembly_is_skipped()
    {
        var modules = _temp.Combine("modules");
        Directory.CreateDirectory(Path.Combine(modules, "Empty"));

        Assert.Empty(ModuleLoader.Load(modules, _log));
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public void Missing_modules_folder_gives_no_modules()
    {
        Assert.Empty(ModuleLoader.Load(_temp.Combine("missing"), _log));
    }

    [Fact]
    public void Module_that_fails_to_start_does_not_stop_the_others()
    {
        var host = CreateHost(new EventBus(_log));
        var healthy = new FakeModule("healthy");

        host.Start([new FakeModule("failing", failOnStart: true), healthy]);

        Assert.Equal([healthy], host.Running);
        Assert.Single(_log.Errors);
    }

    [Fact]
    public void Subscriptions_of_a_failed_module_are_released()
    {
        var bus = new EventBus(_log);
        var host = CreateHost(bus);
        var failing = new FakeModule("failing", failOnStart: true);

        host.Start([failing]);
        bus.Publish(new AppStarted());

        Assert.Equal(0, failing.EventsReceived);
    }

    [Fact]
    public void Duplicate_id_is_rejected()
    {
        var host = CreateHost(new EventBus(_log));
        var first = new FakeModule("same");

        host.Start([first, new FakeModule("same")]);

        Assert.Equal([first], host.Running);
        Assert.Single(_log.Errors);
    }

    [Fact]
    public void Modules_stop_in_reverse_order_even_if_one_fails()
    {
        var stopped = new List<string>();
        var host = CreateHost(new EventBus(_log));

        host.Start(
        [
            new FakeModule("first", onStop: stopped.Add),
            new FakeModule("second", onStop: stopped.Add, failOnStop: true),
            new FakeModule("third", onStop: stopped.Add),
        ]);
        host.StopAll();

        Assert.Equal(["third", "second", "first"], stopped);
        Assert.Empty(host.Running);
    }

    [Fact]
    public void Subscriptions_are_released_when_a_module_stops()
    {
        var bus = new EventBus(_log);
        var host = CreateHost(bus);
        var module = new FakeModule("subscriber");
        host.Start([module]);

        bus.Publish(new AppStarted());
        host.StopAll();
        bus.Publish(new AppStarted());

        Assert.Equal(1, module.EventsReceived);
    }

    [Fact]
    public void Host_passes_developer_mode_and_isolates_module_saves()
    {
        var bus = new EventBus(_log);
        var data = new GameData(_temp.Path, _log);
        var store = new JsonFileStore(_temp.Combine("store.json"), _log);
        var economy = new CoinEconomy(store, bus, TimeProvider.System);
        var host = new ModuleHost(bus, data, new TextCatalog(data, "ru", _log), economy, store, store, new RecoveryService(new ChangeJournal(_temp.Combine("recovery.json"), _log), () => [], _log), _log, developerMode: true);
        var module = new FakeModule("saver");

        host.Start([module]);
        module.Context!.Saves.Set("value", 5);

        Assert.True(module.Context.DeveloperMode);
        Assert.Equal(5, store.Get<int>("modules/saver/value"));
    }

    [Fact]
    public void Stopped_module_loses_its_income_modifiers()
    {
        var bus = new EventBus(_log);
        var data = new GameData(_temp.Path, _log);
        var store = new JsonFileStore(_temp.Combine("store.json"), _log);
        var economy = new CoinEconomy(store, bus, TimeProvider.System);
        var host = new ModuleHost(bus, data, new TextCatalog(data, "ru", _log), economy, store, store, new RecoveryService(new ChangeJournal(_temp.Combine("recovery.json"), _log), () => [], _log), _log, developerMode: false);
        var module = new FakeModule("booster");
        host.Start([module]);

        module.Context!.Economy.AddModifier(new IncomeModifier(IncomeStep.Upgrade, 5m));
        Assert.Equal(5m, economy.Preview("click", 1));

        host.StopAll();

        Assert.Equal(1m, economy.Preview("click", 1));
    }

    private sealed class FakeModule(
        string id,
        bool failOnStart = false,
        bool failOnStop = false,
        Action<string>? onStop = null) : IModule
    {
        public string Id => id;
        public string Name => id;
        public int EventsReceived { get; private set; }
        public IModuleContext? Context { get; private set; }

        public void Start(IModuleContext context)
        {
            Context = context;
            context.Events.Subscribe<AppStarted>(_ => EventsReceived++);
            if (failOnStart)
            {
                throw new InvalidOperationException();
            }
        }

        public void Stop()
        {
            onStop?.Invoke(id);
            if (failOnStop)
            {
                throw new InvalidOperationException();
            }
        }
    }
}
