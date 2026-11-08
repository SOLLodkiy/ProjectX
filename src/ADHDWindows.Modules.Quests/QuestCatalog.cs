using ADHDWindows.Core.Data;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Quests;

namespace ADHDWindows.Modules.Quests;

/// <summary>Виды заданий: что считается прогрессом.</summary>
public static class QuestTypes
{
    /// <summary>Клики, засчитанные защитой от автокликеров. <c>button</c> — только эта кнопка.</summary>
    public const string Clicks = "clicks";

    /// <summary>Хронометраж в минутах. <c>source</c> — video, music или game, без него — любой.</summary>
    public const string Media = "media";

    /// <summary>Минуты в одной установленной и купленной игре, её выбирают при выдаче.</summary>
    public const string Game = "game";

    /// <summary>Потратить монетки на что угодно.</summary>
    public const string Spend = "spend";

    /// <summary>Купить уровни улучшений в магазине.</summary>
    public const string Upgrade = "upgrade";

    /// <summary>Купить программы, игры или сайты. <c>kind</c> — program, game или site, без него — любое.</summary>
    public const string Unlock = "unlock";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Clicks, Media, Game, Spend, Upgrade, Unlock };
}

/// <summary>
/// Шаблон задания из data/quests. <paramref name="Goal"/> — в штуках, монетках или минутах, как у типа.
/// В <paramref name="Title"/> подставляются <c>{goal}</c> и <c>{game}</c>.
/// </summary>
public sealed record QuestTemplate(
    string Id,
    QuestTier Tier,
    string Type,
    double Goal,
    long RewardMin,
    long RewardMax,
    string Title,
    string Icon,
    int Weight = 1,
    string? Button = null,
    string? Source = null,
    string? Kind = null)
{
    public QuestUnit Unit => Type switch
    {
        QuestTypes.Media or QuestTypes.Game => QuestUnit.Seconds,
        QuestTypes.Spend => QuestUnit.Coins,
        _ => QuestUnit.Count,
    };

    /// <summary>Цель в единицах прогресса: минуты переводятся в секунды.</summary>
    public double GoalValue => Unit == QuestUnit.Seconds ? Goal * 60 : Goal;
}

/// <summary>Читает шаблоны из всех файлов data/quests. Неверная запись пропускается и не мешает остальным.</summary>
public static class QuestCatalog
{
    public const string DataPrefix = "quests/";

    private static readonly string[] Sources = ["video", "music", "game"];
    private static readonly string[] Buttons = ["left", "right", "middle"];
    private static readonly string[] Kinds = ["program", "game", "site"];

    public static IReadOnlyList<QuestTemplate> Load(GameData data, ILog log)
    {
        var templates = new List<QuestTemplate>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in data.Keys.Where(key => key.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            if (!data.TryGet<QuestFile>(key, out var file) || file?.Templates is null)
            {
                log.Error($"Файл заданий {key} не прочитан");
                continue;
            }

            foreach (var entry in file.Templates)
            {
                var problem = Check(entry);
                if (problem is not null)
                {
                    log.Error($"Шаблон задания {entry.Id ?? "без кода"} в {key} пропущен: {problem}");
                    continue;
                }

                if (!ids.Add(entry.Id!))
                {
                    log.Error($"Шаблон задания {entry.Id} в {key} пропущен: код повторяется");
                    continue;
                }

                templates.Add(new QuestTemplate(
                    entry.Id!,
                    ParseTier(entry.Tier!)!.Value,
                    entry.Type!,
                    entry.Goal,
                    entry.Reward![0],
                    entry.Reward[1],
                    entry.Title!,
                    string.IsNullOrWhiteSpace(entry.Icon) ? "quest" : entry.Icon,
                    Math.Max(1, entry.Weight ?? 1),
                    entry.Button,
                    entry.Source,
                    entry.Kind));
            }
        }

        return templates;
    }

    public static QuestTier? ParseTier(string text) => text switch
    {
        "easy" => QuestTier.Easy,
        "medium" => QuestTier.Medium,
        "hard" => QuestTier.Hard,
        _ => null,
    };

    private static string? Check(QuestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            return "нет кода";
        }

        if (entry.Tier is null || ParseTier(entry.Tier) is null)
        {
            return $"неизвестная сложность {entry.Tier}";
        }

        if (entry.Type is null || !QuestTypes.All.Contains(entry.Type))
        {
            return $"неизвестный тип {entry.Type}";
        }

        if (!(entry.Goal > 0) || !double.IsFinite(entry.Goal))
        {
            return "цель должна быть больше нуля";
        }

        if (entry.Reward is not [var min, var max] || min <= 0 || max < min)
        {
            return "награда — два числа [от, до], от больше нуля";
        }

        if (string.IsNullOrWhiteSpace(entry.Title))
        {
            return "нет текста";
        }

        if (entry.Button is not null && !Buttons.Contains(entry.Button))
        {
            return $"неизвестная кнопка {entry.Button}";
        }

        if (entry.Source is not null && !Sources.Contains(entry.Source))
        {
            return $"неизвестный источник {entry.Source}";
        }

        if (entry.Kind is not null && !Kinds.Contains(entry.Kind))
        {
            return $"неизвестный вид покупки {entry.Kind}";
        }

        return entry.Type == QuestTypes.Game && !entry.Title.Contains("{game}", StringComparison.Ordinal)
            ? "в тексте задания на игру нет {game}"
            : null;
    }

    private sealed record QuestFile(List<QuestEntry>? Templates);

    private sealed record QuestEntry(
        string? Id,
        string? Tier,
        string? Type,
        double Goal,
        long[]? Reward,
        string? Title,
        string? Icon,
        int? Weight,
        string? Button,
        string? Source,
        string? Kind);
}
