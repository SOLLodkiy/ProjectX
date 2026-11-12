using ADHDWindows.Core.Data;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Modules.Pet;

/// <summary>
/// Правила питомца из data/cosmetics/pet.json. <see cref="Levels"/> — сколько порций всего нужно съесть для каждого уровня,
/// начиная с первого (0). <see cref="Scales"/> — увеличение картинки на каждом уровне: питомец растёт.
/// </summary>
public sealed record PetRules(
    long FeedPrice,
    IReadOnlyList<long> Levels,
    IReadOnlyList<int> Scales,
    double SleepMinutes,
    double HappySeconds,
    double EatSeconds,
    int Fps)
{
    public const string DataKey = "cosmetics/pet";

    public static PetRules Default { get; } = new(25, [0, 3, 8, 15, 25], [2, 2, 3, 3, 4], 3, 4, 3, 4);

    public int MaxLevel => Levels.Count;

    public static PetRules Load(GameData data, ILog log)
    {
        if (!data.TryGet<PetRules>(DataKey, out var rules) || rules is null)
        {
            log.Warn($"Нет правил питомца {DataKey}, действуют значения по умолчанию");
            return Default;
        }

        var valid = rules.FeedPrice > 0
            && rules.Levels is { Count: > 0 } levels && levels[0] == 0 && levels.Zip(levels.Skip(1)).All(pair => pair.First < pair.Second)
            && rules.Scales is { } scales && scales.Count == levels.Count && scales.All(scale => scale is >= 1 and <= 6)
            && rules.SleepMinutes > 0 && rules.HappySeconds > 0 && rules.EatSeconds > 0 && rules.Fps > 0;
        if (!valid)
        {
            log.Warn($"Правила питомца {DataKey} неверны, действуют значения по умолчанию");
            return Default;
        }

        return rules;
    }

    /// <summary>Уровень питомца, который съел <paramref name="food"/> порций: от 1 до <see cref="MaxLevel"/>.</summary>
    public int LevelFor(long food) => Math.Max(1, Levels.Count(threshold => threshold <= food));

    /// <summary>Сколько порций всего нужно для следующего уровня, null — уровень максимальный.</summary>
    public long? NextFor(long food)
    {
        var level = LevelFor(food);
        return level >= MaxLevel ? null : Levels[level];
    }

    public int ScaleFor(long food) => Scales[LevelFor(food) - 1];
}
