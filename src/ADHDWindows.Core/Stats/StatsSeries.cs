namespace ADHDWindows.Core.Stats;

/// <summary>Точка графика: начало дня или недели и значение за этот промежуток.</summary>
public sealed record SeriesPoint(DateOnly Start, long Value);

/// <summary>Ряды для графиков статистики. Дни без записей дают нули, поэтому пропуски видны на графике.</summary>
public static class StatsSeries
{
    /// <summary>Последние <paramref name="count"/> дней по <paramref name="today"/> включительно.</summary>
    public static IReadOnlyList<SeriesPoint> Days(IEnumerable<DayStats> days, DateOnly today, int count, Func<DayStats, long> value)
    {
        var byDate = days.GroupBy(day => day.Date).ToDictionary(group => group.Key, group => group.Sum(value));
        return [.. Enumerable.Range(0, count)
            .Select(index => today.AddDays(index - count + 1))
            .Select(date => new SeriesPoint(date, byDate.GetValueOrDefault(date)))];
    }

    /// <summary>Последние <paramref name="count"/> недель с понедельника, текущая неделя последняя.</summary>
    public static IReadOnlyList<SeriesPoint> Weeks(IEnumerable<DayStats> days, DateOnly today, int count, Func<DayStats, long> value)
    {
        var thisWeek = WeekStart(today);
        var byWeek = days.GroupBy(day => WeekStart(day.Date)).ToDictionary(group => group.Key, group => group.Sum(value));
        return [.. Enumerable.Range(0, count)
            .Select(index => thisWeek.AddDays(7 * (index - count + 1)))
            .Select(start => new SeriesPoint(start, byWeek.GetValueOrDefault(start)))];
    }

    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
