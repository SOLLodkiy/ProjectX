namespace ADHDWindows.Core.Upgrades;

/// <summary>
/// Помощь модулям, которые применяют эффекты. Эффект — код в поле <c>effect</c> записи улучшения.
/// Новое улучшение на готовом эффекте добавляется одной записью в data/upgrades без изменения кода.
/// </summary>
public static class UpgradeEffects
{
    /// <summary>
    /// Прибавка к базе дохода: <c>amount</c> монеток за уровень для источников <c>source</c>
    /// (строка или список, например <c>click</c>, <c>video</c>).
    /// </summary>
    public const string IncomeBonus = "income.bonus";

    /// <summary>
    /// Множитель улучшений: <c>+percent</c> процентов за уровень для источников <c>source</c>.
    /// Множители разных улучшений перемножаются.
    /// </summary>
    public const string IncomeMultiplier = "income.multiplier";

    /// <summary>Улучшения с эффектом <paramref name="effect"/>.</summary>
    public static IEnumerable<UpgradeLevel> Of(IEnumerable<UpgradeLevel> owned, string effect) =>
        owned.Where(item => item.Level > 0 && string.Equals(item.Upgrade.Effect, effect, StringComparison.Ordinal));

    /// <summary>Сумма «уровень × параметр» по всем улучшениям с эффектом.</summary>
    public static decimal Total(IEnumerable<UpgradeLevel> owned, string effect, string parameter) =>
        Of(owned, effect).Sum(item => item.Level * item.Upgrade.Number(parameter));

    /// <summary>Есть ли хоть одно улучшение с эффектом, у которого параметр <paramref name="parameter"/> содержит <paramref name="value"/>.</summary>
    public static bool Has(IEnumerable<UpgradeLevel> owned, string effect, string parameter, string value) =>
        Of(owned, effect).Any(item => item.Upgrade.Strings(parameter).Contains(value, StringComparer.Ordinal));
}
