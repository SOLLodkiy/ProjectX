using ADHDWindows.Core.Data;

namespace ADHDWindows.Modules.Time;

/// <summary>Чем занят человек. Каждая активность — свой источник дохода.</summary>
public enum Activity
{
    Video,
    Music,
    Game,
}

public sealed record ActivityRates(decimal Video, decimal Music, decimal Game);

public sealed record PresenceLimits(double Video, double Game);

/// <summary>Сайт с видео или музыкой. <paramref name="Path"/> ограничивает запись началом пути страницы.</summary>
public sealed record SiteRule(string Domain, string Kind, string? Path = null);

/// <summary>Настольный клиент. <paramref name="Match"/> ищется в коде приложения системной сессии мультимедиа.</summary>
public sealed record AppRule(string Match, string Name, string Kind);

/// <summary>Правила хронометража из data/time/rewards.json.</summary>
public sealed record TimeRules(
    ActivityRates Rates,
    decimal CompletionBonus,
    double CompletionShare,
    double MinCompletionSeconds,
    IReadOnlyList<decimal> RepeatFactors,
    PresenceLimits PresenceMinutes,
    IReadOnlyList<SiteRule> Sites,
    IReadOnlyList<AppRule> Apps)
{
    public const string DataKey = "time/rewards";

    public static TimeRules Load(GameData data)
    {
        if (!data.TryGet<TimeRules>(DataKey, out var rules) || rules is null)
        {
            throw new InvalidDataException($"Нет правил {DataKey}");
        }

        rules.Validate();
        return rules;
    }

    public static string SourceOf(Activity activity) => activity switch
    {
        Activity.Video => "video",
        Activity.Music => "music",
        _ => "game",
    };

    public void Validate()
    {
        if (Rates is null || Rates.Video < 0 || Rates.Music < 0 || Rates.Game < 0)
        {
            throw new InvalidDataException("Неверные расценки хронометража");
        }

        if (CompletionBonus < 0 || CompletionShare is <= 0 or > 1 || MinCompletionSeconds < 0)
        {
            throw new InvalidDataException("Неверный бонус за досмотр");
        }

        if (RepeatFactors is null || RepeatFactors.Count == 0 || RepeatFactors.Any(factor => factor is < 0 or > 1))
        {
            throw new InvalidDataException("Неверные множители повторов");
        }

        if (PresenceMinutes is null || PresenceMinutes.Video <= 0 || PresenceMinutes.Game <= 0)
        {
            throw new InvalidDataException("Неверное время до вопроса «Ты тут?»");
        }

        if (Sites.Any(site => Parse(site.Kind) is not (Activity.Video or Activity.Music)
                || string.IsNullOrWhiteSpace(site.Domain)
                || site.Domain != site.Domain.Trim().ToLowerInvariant()
                || site.Domain.Contains('/')
                || site.Path is { } path && !path.StartsWith('/'))
            || Sites.DistinctBy(site => (site.Domain, site.Path)).Count() != Sites.Count)
        {
            throw new InvalidDataException("В списке сайтов хронометража есть ошибка или повтор");
        }

        // Настольные клиенты играют фоном, а связать их сессию с окном нельзя. Поэтому только музыка.
        if (Apps.Any(app => Parse(app.Kind) != Activity.Music || string.IsNullOrWhiteSpace(app.Match)))
        {
            throw new InvalidDataException("В списке приложений хронометража есть ошибка");
        }
    }

    /// <summary>Правила с дополнительными сайтами. Проверяются так же, как сайты из данных.</summary>
    public TimeRules WithSites(IEnumerable<SiteRule> extra)
    {
        var rules = this with { Sites = [.. Sites, .. extra] };
        rules.Validate();
        return rules;
    }

    /// <summary>Разбирает «домен=вид, домен=вид».</summary>
    public static IEnumerable<SiteRule> ParseSites(string text) =>
        text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .Select(parts => new SiteRule(parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant()));

    public decimal RateOf(Activity activity) => activity switch
    {
        Activity.Video => Rates.Video,
        Activity.Music => Rates.Music,
        _ => Rates.Game,
    };

    /// <summary>Множитель для <paramref name="number"/>-го раза подряд, начиная с 1.</summary>
    public decimal RepeatFactor(int number) => RepeatFactors[Math.Clamp(number - 1, 0, RepeatFactors.Count - 1)];

    public TimeSpan PresenceLimit(Activity activity) =>
        TimeSpan.FromMinutes(activity == Activity.Game ? PresenceMinutes.Game : PresenceMinutes.Video);

    /// <summary>Чем считается страница. Null — сайт не приносит монет. Побеждает самая точная запись.</summary>
    public Activity? Classify(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        var path = uri.AbsolutePath;
        SiteRule? best = null;
        foreach (var site in Sites)
        {
            if ((host == site.Domain || host.EndsWith("." + site.Domain, StringComparison.Ordinal))
                && (site.Path is null || PathMatches(path, site.Path))
                && (best is null
                    || site.Domain.Length > best.Domain.Length
                    || (site.Domain.Length == best.Domain.Length && (site.Path?.Length ?? 0) > (best.Path?.Length ?? 0))))
            {
                best = site;
            }
        }

        return best is null ? null : Parse(best.Kind);
    }

    /// <summary>Настольный клиент по коду приложения сессии мультимедиа, или null.</summary>
    public AppRule? FindApp(string appId) =>
        Apps.FirstOrDefault(app => appId.Contains(app.Match, StringComparison.OrdinalIgnoreCase));

    // Простое начало пути: у VK видео лежат по адресам /video-1_2, а музыка — /audios1.
    private static bool PathMatches(string path, string prefix) => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static Activity? Parse(string? kind) => kind switch
    {
        "video" => Activity.Video,
        "music" => Activity.Music,
        _ => null,
    };
}
