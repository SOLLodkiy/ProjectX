using ADHDWindows.Core.Input;
using ADHDWindows.Modules.Clicks;

namespace ADHDWindows.Tests;

public sealed class ClickFilterTests
{
    private readonly ClickFilter _filter = new();

    private static MouseButtonChanged Left(double ms, bool pressed = true, bool injected = false) =>
        new(MouseButton.Left, pressed, injected, TimeSpan.FromMilliseconds(ms));

    private ClickVerdict Click(double ms)
    {
        var verdict = _filter.Process(Left(ms));
        _filter.Process(Left(ms + 1, pressed: false));
        return verdict;
    }

    /// <summary>Неровный ритм человека: промежутки в пределах ±20 мс от среднего.</summary>
    private static IEnumerable<double> HumanTimes(int count, double perClick = 60)
    {
        var random = new Random(42);
        var time = 0.0;
        for (var i = 0; i < count; i++)
        {
            yield return time;
            time += perClick + (random.NextDouble() * 40) - 20;
        }
    }

    [Fact]
    public void Press_counts_once_and_release_is_not_a_click()
    {
        Assert.Equal(ClickVerdict.Counted, _filter.Process(Left(0)));
        Assert.Equal(ClickVerdict.NotAClick, _filter.Process(Left(50, pressed: false)));
    }

    [Fact]
    public void Holding_gives_nothing_until_release()
    {
        Assert.Equal(ClickVerdict.Counted, _filter.Process(Left(0)));
        Assert.Equal(ClickVerdict.Held, _filter.Process(Left(500)));
        Assert.Equal(ClickVerdict.Held, _filter.Process(Left(1000)));
        _filter.Process(Left(1500, pressed: false));
        Assert.Equal(ClickVerdict.Counted, _filter.Process(Left(1600)));
    }

    [Fact]
    public void Lost_release_costs_at_most_one_click()
    {
        Assert.Equal(ClickVerdict.Counted, _filter.Process(Left(0)));
        Assert.Equal(ClickVerdict.Held, _filter.Process(Left(1000)));
        _filter.Process(Left(1100, pressed: false));
        Assert.Equal(ClickVerdict.Counted, _filter.Process(Left(1200)));
    }

    [Theory]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    public void Other_buttons_are_not_clicks(MouseButton button) =>
        Assert.Equal(ClickVerdict.NotAClick, _filter.Process(new MouseButtonChanged(button, true, false, TimeSpan.Zero)));

    [Fact]
    public void Synthetic_clicks_are_not_counted()
    {
        Assert.Equal(ClickVerdict.NotAClick, _filter.Process(Left(0, injected: true)));
        Assert.Equal(ClickVerdict.NotAClick, _filter.Process(Left(1, pressed: false, injected: true)));
    }

    [Fact]
    public void Synthetic_release_does_not_reset_a_held_button()
    {
        Assert.Equal(ClickVerdict.Counted, _filter.Process(Left(0)));
        _filter.Process(Left(10, pressed: false, injected: true));
        Assert.Equal(ClickVerdict.Held, _filter.Process(Left(20)));
    }

    [Fact]
    public void Synthetic_press_does_not_block_a_real_click()
    {
        _filter.Process(Left(0, injected: true));
        Assert.Equal(ClickVerdict.Counted, _filter.Process(Left(10)));
    }

    [Fact]
    public void Clicks_above_the_rate_limit_earn_nothing()
    {
        var verdicts = HumanTimes(60, perClick: 30).Select(Click).ToList();

        Assert.Equal(ClickFilter.MaxClicksPerSecond, verdicts.TakeWhile(verdict => verdict == ClickVerdict.Counted).Count());
        Assert.All(verdicts.Skip(ClickFilter.MaxClicksPerSecond), verdict => Assert.Equal(ClickVerdict.TooFast, verdict));
    }

    [Fact]
    public void Rate_limit_ends_after_slowing_down()
    {
        var times = HumanTimes(30, perClick: 30).ToList();
        times.ForEach(time => Click(time));

        Assert.Equal(ClickVerdict.Counted, Click(times[^1] + 1100));
    }

    [Fact]
    public void Fast_uneven_human_clicking_is_fully_counted()
    {
        Assert.All(HumanTimes(500), time => Assert.Equal(ClickVerdict.Counted, Click(time)));
    }

    [Fact]
    public void Slow_even_series_stops_earning()
    {
        var verdicts = Enumerable.Range(0, 40).Select(i => Click(i * 200.0)).ToList();

        Assert.All(verdicts.Take(ClickFilter.EvenSeriesIntervals), verdict => Assert.Equal(ClickVerdict.Counted, verdict));
        Assert.All(verdicts.Skip(ClickFilter.EvenSeriesIntervals), verdict => Assert.Equal(ClickVerdict.TooEven, verdict));
    }

    [Fact]
    public void Device_noise_does_not_hide_an_even_series()
    {
        var random = new Random(7);
        var verdicts = Enumerable.Range(0, 40).Select(i => Click((i * 200.0) + (random.NextDouble() * 6))).ToList();

        Assert.Equal(ClickVerdict.TooEven, verdicts[^1]);
    }

    [Fact]
    public void One_uneven_click_does_not_end_the_penalty()
    {
        var time = 0.0;
        for (var i = 0; i < 20; i++, time += 200)
        {
            Click(time);
        }

        Assert.Equal(ClickVerdict.TooEven, Click(time + 700));
    }

    [Fact]
    public void Penalty_ends_after_a_pause()
    {
        var time = 0.0;
        for (var i = 0; i < 20; i++, time += 200)
        {
            Click(time);
        }

        Assert.Equal(ClickVerdict.Counted, Click(time + ClickFilter.EvenSeriesPenalty.TotalMilliseconds));
    }
}
