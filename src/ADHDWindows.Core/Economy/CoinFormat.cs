using System.Globalization;

namespace ADHDWindows.Core.Economy;

/// <summary>Запись сумм для интерфейса.</summary>
public static class CoinFormat
{
    private const long ShortFrom = 10_000;

    private static readonly (long Unit, string Suffix)[] Units =
    [
        (1_000_000_000_000_000_000, "E"),
        (1_000_000_000_000_000, "Q"),
        (1_000_000_000_000, "T"),
        (1_000_000_000, "B"),
        (1_000_000, "M"),
        (1_000, "K"),
    ];

    /// <summary>
    /// Сокращает суммы от 10 000: <c>12.4K</c>. Лишний знак отбрасывается без округления,
    /// чтобы на экране не было больше монеток, чем на балансе.
    /// </summary>
    public static string Short(long value)
    {
        if (value < ShortFrom)
        {
            return Plain(value);
        }

        foreach (var (unit, suffix) in Units)
        {
            if (value >= unit)
            {
                var tenths = value / (unit / 10);
                var fraction = tenths % 10;
                return fraction == 0 ? Plain(tenths / 10) + suffix : $"{Plain(tenths / 10)}.{Plain(fraction)}{suffix}";
            }
        }

        return Plain(value);
    }

    /// <summary>Сумма начисления со знаком: <c>+1</c>.</summary>
    public static string Gain(long value) => "+" + Short(value);

    private static string Plain(long value) => value.ToString(CultureInfo.InvariantCulture);
}
