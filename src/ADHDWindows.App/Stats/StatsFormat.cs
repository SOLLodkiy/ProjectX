using System.Globalization;
using ADHDWindows.Core.Data;

namespace ADHDWindows.App.Stats;

/// <summary>Подписи чисел в окне статистики.</summary>
internal static class StatsFormat
{
    public static string Duration(TextCatalog texts, double seconds)
    {
        var minutes = (long)Math.Floor(Math.Max(0, seconds) / 60);
        return minutes >= 60
            ? texts.Get("stats.hours", minutes / 60, minutes % 60)
            : texts.Get("stats.minutes", minutes);
    }

    /// <summary>Метры с одним знаком после запятой, тысячи без дробной части.</summary>
    public static string Meters(double meters) =>
        meters >= 1000 ? Math.Floor(meters).ToString("N0", CultureInfo.CurrentCulture) : meters.ToString("0.0", CultureInfo.CurrentCulture);

    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Percent(double share) =>
        (Math.Floor(Math.Clamp(share, 0, 1) * 1000) / 10).ToString("0.#", CultureInfo.CurrentCulture);

    public static string Date(DateTimeOffset when) => when.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);

    public static string Moment(DateTimeOffset when) => when.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Подпись из текстов, а если её нет — сам код: новая механика видна и без своего текста.</summary>
    public static string Label(TextCatalog texts, string key, string fallback)
    {
        var text = texts.Get(key);
        return text == key ? fallback : text;
    }
}
