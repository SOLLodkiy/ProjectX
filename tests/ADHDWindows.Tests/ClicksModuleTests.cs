using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Clicks;

namespace ADHDWindows.Tests;

public sealed class ClicksModuleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly EventBus _bus;
    private readonly CoinEconomy _economy;
    private readonly ModuleHost _host;

    public ClicksModuleTests()
    {
        _bus = new EventBus(_log);
        var data = new GameData(_temp.Path, _log);
        var store = new JsonFileStore(_temp.Combine("store.json"), _log);
        _economy = new CoinEconomy(store, _bus, TimeProvider.System);
        _host = new ModuleHost(_bus, data, new TextCatalog(data, "ru", _log), _economy, store, store, _log, developerMode: false);

        // Модуль загружается из папки, как в программе.
        var modules = _temp.Combine("modules");
        RepositoryRoot.DeployModule("ADHDWindows.Modules.Clicks", modules);
        _host.Start(ModuleLoader.Load(modules, _log));
    }

    public void Dispose()
    {
        _host.StopAll();
        _temp.Dispose();
    }

    private void Publish(double ms, bool pressed = true, bool injected = false, MouseButton button = MouseButton.Left) =>
        _bus.Publish(new MouseButtonChanged(button, pressed, injected, TimeSpan.FromMilliseconds(ms)));

    [Fact]
    public void Module_loads_from_its_folder()
    {
        Assert.Equal("clicks", Assert.Single(_host.Running).Id);
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Click_gives_one_coin()
    {
        Publish(0);
        Publish(50, pressed: false);

        Assert.Equal(1, _economy.Balance);
    }

    [Fact]
    public void Holding_gives_one_coin_once()
    {
        Publish(0);
        Publish(1000);
        Publish(2000);

        Assert.Equal(1, _economy.Balance);
    }

    [Fact]
    public void Synthetic_clicks_give_nothing()
    {
        Publish(0, injected: true);
        Publish(50, pressed: false, injected: true);

        Assert.Equal(0, _economy.Balance);
    }

    [Fact]
    public void Right_button_gives_nothing()
    {
        Publish(0, button: MouseButton.Right);

        Assert.Equal(0, _economy.Balance);
    }

    [Fact]
    public void Income_goes_through_the_common_formula()
    {
        _economy.AddModifier(new IncomeModifier(IncomeStep.Bonus, 1, ClicksModule.IncomeSource));
        _economy.AddModifier(new IncomeModifier(IncomeStep.Hardship, 0.5m));

        Publish(0);
        Publish(10, pressed: false);
        Publish(300);

        Assert.Equal(2, _economy.Balance);
    }

    [Fact]
    public void Stopped_module_does_not_earn()
    {
        _host.StopAll();

        Publish(0);

        Assert.Equal(0, _economy.Balance);
    }

    [Fact]
    public void Rejected_series_is_logged_once()
    {
        for (var i = 0; i < 40; i++)
        {
            Publish(i * 200.0);
            Publish((i * 200.0) + 10, pressed: false);
        }

        Assert.Equal(ClickFilter.EvenSeriesIntervals, _economy.Balance);
        Assert.Single(_log.Warnings);
    }
}
