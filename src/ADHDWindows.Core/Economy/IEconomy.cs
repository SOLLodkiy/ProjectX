namespace ADHDWindows.Core.Economy;

/// <summary>Шаг общей формулы дохода. Шаги применяются по порядку значений.</summary>
public enum IncomeStep
{
    /// <summary>Прибавка к базе источника.</summary>
    Bonus,

    /// <summary>Множитель улучшений.</summary>
    Upgrade,

    /// <summary>Множитель усложнений, применяется после улучшений.</summary>
    Hardship,
}

/// <summary>Правка формулы дохода. <paramref name="Source"/> равен null, если правка действует на все источники.</summary>
public sealed record IncomeModifier(IncomeStep Step, decimal Value, string? Source = null);

/// <summary>Начисление. <paramref name="Amount"/> — целые монетки, зачисленные на баланс.</summary>
public sealed record CoinsEarned(string Source, long Amount, long Balance);

/// <summary>Трата с баланса.</summary>
public sealed record CoinsSpent(string Reason, long Amount, long Balance);

/// <summary>Кошелёк и единая формула дохода. Все методы можно вызывать из любого потока.</summary>
public interface IEconomy
{
    long Balance { get; }

    /// <summary>Сколько монеток начислено за последнюю минуту.</summary>
    long IncomePerMinute { get; }

    /// <summary>Доход источника после всей формулы, без зачисления.</summary>
    decimal Preview(string source, decimal baseAmount);

    /// <summary>
    /// Проводит доход через формулу и зачисляет его. Дробная часть копится и попадёт в следующее начисление.
    /// Возвращает зачисленные целые монетки.
    /// </summary>
    long Earn(string source, decimal baseAmount);

    /// <summary>Списывает монетки целиком, а если их не хватает, не меняет баланс и возвращает false.</summary>
    bool TrySpend(long amount, string reason);

    /// <summary>Добавляет правку формулы. Dispose снимает её.</summary>
    IDisposable AddModifier(IncomeModifier modifier);
}
