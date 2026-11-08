using System.Globalization;
using System.Text.Json;

namespace ADHDWindows.Core.Upgrades;

/// <summary>
/// Улучшение из data/upgrades. <paramref name="Effect"/> — код эффекта, который понимает какой-то модуль,
/// а <paramref name="Params"/> — его настройки. <paramref name="Requires"/> — уровни других улучшений, нужные для покупки.
/// Цена следующего уровня: <c>Price × Growth ^ текущий уровень</c>.
/// </summary>
public sealed record UpgradeDefinition(
    string Id,
    string Branch,
    string Name,
    string Description,
    string Icon,
    string Effect,
    IReadOnlyDictionary<string, JsonElement> Params,
    long Price,
    decimal Growth,
    int MaxLevel,
    IReadOnlyDictionary<string, int> Requires)
{
    /// <summary>Цена покупки уровня <paramref name="level"/> + 1. Слишком большая цена упирается в предел типа.</summary>
    public long PriceAt(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        try
        {
            decimal price = Price;
            for (var step = 0; step < level; step++)
            {
                price *= Growth;
                if (price >= long.MaxValue)
                {
                    return long.MaxValue;
                }
            }

            return (long)Math.Round(price, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>Числовой параметр эффекта. Отсутствующий или нечисловой параметр даёт <paramref name="fallback"/>.</summary>
    public decimal Number(string name, decimal fallback = 0) =>
        Params.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : fallback;

    /// <summary>Строковый параметр или список строк. Одна строка возвращается списком из одного элемента.</summary>
    public IReadOnlyList<string> Strings(string name)
    {
        if (!Params.TryGetValue(name, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Array => value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToList(),
            _ => [],
        };
    }

    public string? Text(string name) => Strings(name) is [var single] ? single : null;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Id} ({Effect})");
}

/// <summary>Ветка магазина: один файл data/upgrades/*.json.</summary>
public sealed record UpgradeBranch(string Id, string Name, string Icon, int Order, IReadOnlyList<UpgradeDefinition> Upgrades);
