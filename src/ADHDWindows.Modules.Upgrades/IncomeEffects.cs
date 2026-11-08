using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Upgrades;

namespace ADHDWindows.Modules.Upgrades;

/// <summary>
/// Общие эффекты дохода: прибавка к базе и множитель улучшений для любых источников.
/// Ими пользуются улучшения кликов, хронометража и будущих источников без отдельного кода.
/// </summary>
public sealed class IncomeEffects(IEconomy economy) : IDisposable
{
    private readonly Lock _lock = new();
    private List<IDisposable> _applied = [];

    public void Apply(IReadOnlyList<UpgradeLevel> owned)
    {
        var modifiers = new List<IncomeModifier>();
        foreach (var item in UpgradeEffects.Of(owned, UpgradeEffects.IncomeBonus))
        {
            var amount = item.Level * item.Upgrade.Number("amount");
            modifiers.AddRange(Sources(item).Select(source => new IncomeModifier(IncomeStep.Bonus, amount, source)));
        }

        foreach (var item in UpgradeEffects.Of(owned, UpgradeEffects.IncomeMultiplier))
        {
            var factor = Math.Max(0, 1 + (item.Level * item.Upgrade.Number("percent") / 100));
            modifiers.AddRange(Sources(item).Select(source => new IncomeModifier(IncomeStep.Upgrade, factor, source)));
        }

        lock (_lock)
        {
            // Новые правки ставятся до снятия старых, поэтому доход ни на миг не теряет купленное.
            // Двойной учёт возможен лишь между двумя строками ниже, а начисление идёт под замком кошелька.
            var added = modifiers.Select(economy.AddModifier).ToList();
            _applied.ForEach(modifier => modifier.Dispose());
            _applied = added;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _applied.ForEach(modifier => modifier.Dispose());
            _applied = [];
        }
    }

    /// <summary>Без параметра source правка действует на все источники.</summary>
    private static IEnumerable<string?> Sources(UpgradeLevel item)
    {
        IReadOnlyList<string?> sources = item.Upgrade.Strings("source");
        return sources.Count == 0 ? [null] : sources;
    }
}
