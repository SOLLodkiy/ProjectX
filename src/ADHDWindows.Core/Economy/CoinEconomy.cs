using ADHDWindows.Core.Events;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Economy;

/// <summary>
/// Кошелёк в сохранении. Формула для всех источников:
/// <c>(база + прибавки) × множители улучшений × множители усложнений</c>.
/// Баланс не ограничен сверху: при достижении предела типа он просто перестаёт расти.
/// </summary>
public sealed class CoinEconomy(IKeyValueStore saves, IEventBus events, TimeProvider clock) : IEconomy
{
    public const string BalanceKey = "economy/balance";
    public const string RemainderKey = "economy/remainder";

    private readonly Lock _lock = new();
    private readonly List<IncomeModifier> _modifiers = [];
    private readonly IncomeRate _rate = new(clock);
    private long _balance;
    private decimal _remainder;

    public long Balance
    {
        get
        {
            lock (_lock)
            {
                return _balance;
            }
        }
    }

    public long IncomePerMinute
    {
        get
        {
            lock (_lock)
            {
                return _rate.Total;
            }
        }
    }

    /// <summary>Читает кошелёк из сохранения. Вызывается после загрузки файла.</summary>
    public void Load()
    {
        lock (_lock)
        {
            _balance = Math.Max(0, saves.Get<long>(BalanceKey));
            _remainder = Math.Clamp(saves.Get<decimal>(RemainderKey), 0m, 0.999999m);
        }
    }

    public decimal Preview(string source, decimal baseAmount)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_lock)
        {
            return Calculate(source, baseAmount);
        }
    }

    public long Earn(string source, decimal baseAmount)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(baseAmount);

        long credited;
        long balance;
        lock (_lock)
        {
            var total = Math.Min(Calculate(source, baseAmount), long.MaxValue);
            var whole = decimal.Truncate(total);
            _remainder += total - whole;
            if (_remainder >= 1)
            {
                _remainder -= 1;
                whole = Math.Min(whole + 1, long.MaxValue);
            }

            credited = Math.Min((long)whole, long.MaxValue - _balance);
            _balance += credited;
            balance = _balance;
            _rate.Add(credited);
            saves.Set(BalanceKey, _balance);
            saves.Set(RemainderKey, _remainder);
        }

        if (credited > 0)
        {
            events.Publish(new CoinsEarned(source, credited, balance));
        }

        return credited;
    }

    public bool TrySpend(long amount, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        ArgumentNullException.ThrowIfNull(reason);

        long balance;
        lock (_lock)
        {
            if (amount > _balance)
            {
                return false;
            }

            _balance -= amount;
            balance = _balance;
            saves.Set(BalanceKey, _balance);
        }

        if (amount > 0)
        {
            events.Publish(new CoinsSpent(reason, amount, balance));
        }

        return true;
    }

    public IDisposable AddModifier(IncomeModifier modifier)
    {
        ArgumentNullException.ThrowIfNull(modifier);
        if (modifier.Step != IncomeStep.Bonus)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(modifier.Value);
        }

        lock (_lock)
        {
            _modifiers.Add(modifier);
        }

        return new Removal(() =>
        {
            lock (_lock)
            {
                // Записи могут совпадать по значению, поэтому снимается именно этот объект.
                _modifiers.RemoveAt(_modifiers.FindIndex(item => ReferenceEquals(item, modifier)));
            }
        });
    }

    private decimal Calculate(string source, decimal baseAmount)
    {
        try
        {
            var total = baseAmount;
            foreach (var step in Enum.GetValues<IncomeStep>())
            {
                foreach (var modifier in _modifiers)
                {
                    if (modifier.Step == step && (modifier.Source is null || modifier.Source == source))
                    {
                        total = step == IncomeStep.Bonus ? total + modifier.Value : total * modifier.Value;
                    }
                }
            }

            return Math.Max(0, total);
        }
        catch (OverflowException)
        {
            return decimal.MaxValue;
        }
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
