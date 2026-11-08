namespace ADHDWindows.Modules.Hardships.Basic;

/// <summary>
/// Разброс размеров и частот при каждом запуске усложнения: повтор ощущается по-другому, а базовые значения
/// остаются ориентиром. Разброс небольшой — по умолчанию ±<see cref="DefaultFraction"/>.
/// </summary>
public sealed class Spread(Random random, double fraction = Spread.DefaultFraction)
{
    public const double DefaultFraction = 0.15;

    public double Fraction => fraction;

    /// <summary>Значение в пределах <paramref name="value"/> × (1 ± доля разброса).</summary>
    public double Vary(double value) => value * (1 + (((random.NextDouble() * 2) - 1) * fraction));

    /// <summary>То же для целых значений, не меньше 1.</summary>
    public int Vary(int value) => Math.Max(1, (int)Math.Round(Vary((double)value)));

    /// <summary>Диапазон «от и до», оба конца сдвинуты одним множителем: порядок концов сохраняется.</summary>
    public (double Min, double Max) Vary(double min, double max)
    {
        var factor = Vary(1.0);
        return (min * factor, max * factor);
    }
}
