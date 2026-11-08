using System.Globalization;
using System.Text;
using System.Text.Json;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Locks;
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

    /// <summary>Открыть страницу по ссылке <c>url</c>.</summary>
    public const string Visit = "visit";

    /// <summary>Прочитать страницу по ссылке: прокрутить её до конца во вкладке в фокусе.</summary>
    public const string Read = "read";

    /// <summary>Выделить на странице по ссылке слово <c>word</c>.</summary>
    public const string Select = "select";

    /// <summary>Полностью прослушать трек по ссылке. <c>goal</c> — примерная длина трека в минутах, пока плеер не сообщит точную.</summary>
    public const string Listen = "listen";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Clicks, Media, Game, Spend, Upgrade, Unlock, Visit, Read, Select, Listen,
    };

    /// <summary>Задания на страницу: у них обязательна ссылка.</summary>
    public static readonly IReadOnlySet<string> Pages = new HashSet<string>(StringComparer.Ordinal) { Visit, Read, Select, Listen };
}

/// <summary>
/// Шаблон задания из data/quests. <paramref name="Goal"/> — в штуках, монетках или минутах, как у типа.
/// В <paramref name="Title"/> подставляются <c>{goal}</c>, <c>{game}</c> и <c>{word}</c>.
/// У заданий на страницу <paramref name="Url"/> — ссылка, <paramref name="Word"/> — слово для выделения.
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
    string? Kind = null,
    string? Url = null,
    string? Word = null)
{
    /// <summary>Какая доля трека должна прозвучать, чтобы прослушивание считалось полным.</summary>
    public const double ListenShare = 0.9;

    public QuestUnit Unit => Type switch
    {
        QuestTypes.Media or QuestTypes.Game or QuestTypes.Listen => QuestUnit.Seconds,
        QuestTypes.Spend => QuestUnit.Coins,
        _ => QuestUnit.Count,
    };

    /// <summary>
    /// Цель в единицах прогресса: минуты переводятся в секунды, у прослушивания — 90% длины трека,
    /// у остальных заданий на страницу — одно действие.
    /// </summary>
    public double GoalValue => Type switch
    {
        QuestTypes.Listen => Math.Round(Goal * 60 * ListenShare),
        QuestTypes.Visit or QuestTypes.Read or QuestTypes.Select => 1,
        _ => Unit == QuestUnit.Seconds ? Goal * 60 : Goal,
    };

    /// <summary>Код страницы задания из <see cref="PageAddress.Normalize"/>, null у заданий без ссылки.</summary>
    public string? Page => PageAddress.Normalize(Url);
}

/// <summary>Читает шаблоны из всех файлов data/quests. Неверная запись пропускается и не мешает остальным.</summary>
public static class QuestCatalog
{
    public const string DataPrefix = "quests/";

    private static readonly string[] Sources = ["video", "music", "game"];
    private static readonly string[] Buttons = ["left", "right", "middle"];
    private static readonly string[] Kinds = ["program", "game", "site"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

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

            Add(templates, ids, file.Templates, key, log);
        }

        return templates;
    }

    /// <summary>
    /// Шаблоны из массива JSON в формате data/quests, например из настройки режима разработчика.
    /// Неверная запись пропускается так же, как в файлах данных.
    /// </summary>
    public static IReadOnlyList<QuestTemplate> Parse(JsonElement array, string source, ILog log)
    {
        var templates = new List<QuestTemplate>();
        List<QuestEntry>? entries = null;
        try
        {
            if (array.ValueKind == JsonValueKind.Array)
            {
                entries = array.Deserialize<List<QuestEntry>>(Json);
            }
        }
        catch (JsonException exception)
        {
            log.Error($"Шаблоны заданий из {source} не прочитаны", exception);
            return templates;
        }

        if (entries is null)
        {
            log.Error($"Шаблоны заданий из {source} не прочитаны: нужен массив");
            return templates;
        }

        Add(templates, new HashSet<string>(StringComparer.Ordinal), entries, source, log);
        return templates;
    }

    /// <summary>Слово для сравнения выделения: без пробелов и знаков препинания по краям, в нижнем регистре, ё как е.</summary>
    public static string NormalizeWord(string? text)
    {
        var value = (text ?? string.Empty).Normalize(NormalizationForm.FormC);
        var start = 0;
        var end = value.Length;
        while (start < end && IsEdge(value[start]))
        {
            start++;
        }

        while (end > start && IsEdge(value[end - 1]))
        {
            end--;
        }

        var builder = new StringBuilder(end - start);
        foreach (var symbol in value[start..end].ToLower(CultureInfo.InvariantCulture))
        {
            builder.Append(symbol == 'ё' ? 'е' : symbol);
        }

        return builder.ToString();

        static bool IsEdge(char symbol) =>
            char.IsWhiteSpace(symbol) || char.IsPunctuation(symbol) || char.IsSymbol(symbol) || char.IsControl(symbol) || symbol == '­';
    }

    public static QuestTier? ParseTier(string text) => text switch
    {
        "easy" => QuestTier.Easy,
        "medium" => QuestTier.Medium,
        "hard" => QuestTier.Hard,
        _ => null,
    };

    private static void Add(List<QuestTemplate> templates, HashSet<string> ids, List<QuestEntry> entries, string source, ILog log)
    {
        foreach (var entry in entries)
        {
            var problem = Check(entry);
            if (problem is not null)
            {
                log.Error($"Шаблон задания {entry.Id ?? "без кода"} в {source} пропущен: {problem}");
                continue;
            }

            if (!ids.Add(entry.Id!))
            {
                log.Error($"Шаблон задания {entry.Id} в {source} пропущен: код повторяется");
                continue;
            }

            var isPage = QuestTypes.Pages.Contains(entry.Type!);
            templates.Add(new QuestTemplate(
                entry.Id!,
                ParseTier(entry.Tier!)!.Value,
                entry.Type!,
                isPage && entry.Type != QuestTypes.Listen ? 1 : entry.Goal,
                entry.Reward![0],
                entry.Reward[1],
                entry.Title!,
                string.IsNullOrWhiteSpace(entry.Icon) ? "quest" : entry.Icon,
                Math.Max(1, entry.Weight ?? 1),
                entry.Button,
                entry.Source,
                entry.Kind,
                isPage ? entry.Url!.Trim() : null,
                entry.Type == QuestTypes.Select ? entry.Word!.Trim() : null));
        }
    }

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

        // У открытия, чтения и выделения цель — одно действие, goal не нужен.
        var isPage = QuestTypes.Pages.Contains(entry.Type);
        if ((!isPage || entry.Type == QuestTypes.Listen) && (!(entry.Goal > 0) || !double.IsFinite(entry.Goal)))
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

        if (isPage && PageAddress.Normalize(entry.Url) is null)
        {
            return "у задания на страницу нужна ссылка url с http или https";
        }

        if (!isPage && entry.Url is not null)
        {
            return "ссылка url бывает только у заданий visit, read, select и listen";
        }

        if (entry.Type == QuestTypes.Select && NormalizeWord(entry.Word).Length == 0)
        {
            return "у задания на выделение нужно слово word";
        }

        if (entry.Type != QuestTypes.Select && (entry.Word is not null || entry.Title.Contains("{word}", StringComparison.Ordinal)))
        {
            return "слово word и {word} в тексте бывают только у задания select";
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
        string? Kind,
        string? Url = null,
        string? Word = null);
}
