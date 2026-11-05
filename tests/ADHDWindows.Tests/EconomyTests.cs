using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Tests;

public sealed class EconomyTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly ManualClock _clock = new();
    private readonly EventBus _bus;
    private readonly JsonFileStore _store;
    private readonly CoinEconomy _economy;

    public EconomyTests()
    {
        _bus = new EventBus(_log);
        _store = new JsonFileStore(_temp.Combine("save.json"), _log);
        _economy = new CoinEconomy(_store, _bus, _clock);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Earning_adds_to_balance_and_reports_the_credit()
    {
        CoinsEarned? earned = null;
        _bus.Subscribe<CoinsEarned>(value => earned = value);

        Assert.Equal(5, _economy.Earn("click", 5));

        Assert.Equal(5, _economy.Balance);
        Assert.Equal(new CoinsEarned("click", 5, 5), earned);
    }

    [Fact]
    public void Formula_applies_bonus_then_upgrades_then_hardships()
    {
        // Добавление в обратном порядке показывает, что важен шаг, а не очерёдность подключения.
        _economy.AddModifier(new IncomeModifier(IncomeStep.Hardship, 0.5m));
        _economy.AddModifier(new IncomeModifier(IncomeStep.Upgrade, 3m));
        _economy.AddModifier(new IncomeModifier(IncomeStep.Bonus, 2m));

        Assert.Equal(4.5m, _economy.Preview("click", 1));
        Assert.Equal(4, _economy.Earn("click", 1));
    }

    [Fact]
    public void Modifier_for_a_source_does_not_touch_other_sources()
    {
        _economy.AddModifier(new IncomeModifier(IncomeStep.Upgrade, 2m, "click"));

        Assert.Equal(2m, _economy.Preview("click", 1));
        Assert.Equal(1m, _economy.Preview("video", 1));
    }

    [Fact]
    public void Removing_a_modifier_keeps_an_identical_one()
    {
        var first = _economy.AddModifier(new IncomeModifier(IncomeStep.Upgrade, 2m));
        _economy.AddModifier(new IncomeModifier(IncomeStep.Upgrade, 2m));

        first.Dispose();
        first.Dispose();

        Assert.Equal(2m, _economy.Preview("click", 1));
    }

    [Fact]
    public void Fractions_add_up_to_whole_coins()
    {
        var events = 0;
        _bus.Subscribe<CoinsEarned>(_ => events++);
        _economy.AddModifier(new IncomeModifier(IncomeStep.Hardship, 0.75m));

        Assert.Equal(0, _economy.Earn("click", 1));
        Assert.Equal(1, _economy.Earn("click", 1));
        Assert.Equal(1, _economy.Earn("click", 1));
        Assert.Equal(1, _economy.Earn("click", 1));

        Assert.Equal(3, _economy.Balance);
        Assert.Equal(3, events);
    }

    [Fact]
    public void Remainder_survives_a_reload()
    {
        _economy.Earn("click", 0.5m);

        var reloaded = new CoinEconomy(_store, _bus, _clock);
        reloaded.Load();

        Assert.Equal(1, reloaded.Earn("click", 0.5m));
    }

    [Fact]
    public void Spending_needs_the_full_amount()
    {
        CoinsSpent? spent = null;
        _bus.Subscribe<CoinsSpent>(value => spent = value);
        _economy.Earn("click", 10);

        Assert.False(_economy.TrySpend(11, "site"));
        Assert.Equal(10, _economy.Balance);
        Assert.Null(spent);

        Assert.True(_economy.TrySpend(10, "site"));
        Assert.Equal(0, _economy.Balance);
        Assert.Equal(new CoinsSpent("site", 10, 0), spent);
    }

    [Fact]
    public void Invalid_amounts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _economy.Earn("click", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _economy.TrySpend(-1, "site"));
        Assert.Throws<ArgumentOutOfRangeException>(() => _economy.AddModifier(new IncomeModifier(IncomeStep.Upgrade, -1m)));
    }

    [Fact]
    public void Negative_bonus_cannot_make_income_negative()
    {
        _economy.AddModifier(new IncomeModifier(IncomeStep.Bonus, -5m));

        Assert.Equal(0, _economy.Earn("click", 1));
        Assert.Equal(0, _economy.Balance);
    }

    [Fact]
    public void Balance_stops_at_the_type_limit_instead_of_overflowing()
    {
        _economy.AddModifier(new IncomeModifier(IncomeStep.Upgrade, decimal.MaxValue));
        _economy.AddModifier(new IncomeModifier(IncomeStep.Upgrade, decimal.MaxValue));

        _economy.Earn("click", 1);
        _economy.Earn("click", 1);

        Assert.Equal(long.MaxValue, _economy.Balance);
    }

    [Fact]
    public void Income_per_minute_is_a_sliding_window()
    {
        _economy.Earn("click", 10);
        _clock.Advance(TimeSpan.FromSeconds(30));
        _economy.Earn("click", 5);
        Assert.Equal(15, _economy.IncomePerMinute);

        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(5, _economy.IncomePerMinute);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, _economy.IncomePerMinute);
    }

    [Fact]
    public void Clock_moved_back_keeps_counting()
    {
        _economy.Earn("click", 10);
        _clock.Advance(TimeSpan.FromMinutes(-5));
        _economy.Earn("click", 5);

        Assert.Equal(15, _economy.IncomePerMinute);
    }

    [Fact]
    public void Corrupted_negative_balance_becomes_zero()
    {
        _store.Set(CoinEconomy.BalanceKey, -100L);

        _economy.Load();

        Assert.Equal(0, _economy.Balance);
    }

    [Fact]
    public async Task Parallel_earning_and_spending_lose_nothing()
    {
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 5000; index++)
            {
                _economy.Earn("click", 3);
                Assert.True(_economy.TrySpend(1, "site"));
            }
        }));

        await Task.WhenAll(workers);

        Assert.Equal(8 * 5000 * 2, _economy.Balance);
    }

    [Fact]
    public void Balance_survives_a_host_restart()
    {
        var paths = new AppPaths(_temp.Combine("install"), _temp.Combine("user"));
        using (var first = new GameHost(paths, new AppOptions(DeveloperMode: false), _log))
        {
            first.Start();
            first.Economy.Earn("click", 42);
        }

        using var second = new GameHost(paths, new AppOptions(DeveloperMode: false), _log);
        second.Start();

        Assert.Equal(42, second.Economy.Balance);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(9999, "9999")]
    [InlineData(10_000, "10K")]
    [InlineData(12_400, "12.4K")]
    [InlineData(12_499, "12.4K")]
    [InlineData(999_999, "999.9K")]
    [InlineData(1_000_000, "1M")]
    [InlineData(1_500_000_000, "1.5B")]
    [InlineData(2_000_000_000_000, "2T")]
    [InlineData(3_100_000_000_000_000, "3.1Q")]
    [InlineData(long.MaxValue, "9.2E")]
    public void Short_format_never_rounds_up(long value, string expected) =>
        Assert.Equal(expected, CoinFormat.Short(value));

    [Fact]
    public void Gain_has_a_sign() => Assert.Equal("+1", CoinFormat.Gain(1));
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan time) => _now += time;
}
