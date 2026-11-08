using ADHDWindows.Core.Locks;

namespace ADHDWindows.Modules.Locks.Sites;

/// <summary>Решение о сайте. <paramref name="Offer"/> равен null, если сайт бесплатен.</summary>
public sealed record SiteVerdict(LockOffer? Offer, string Reason);

/// <summary>Правило для расширения: домен со всеми поддоменами открыт или закрыт. Побеждает самое длинное подходящее.</summary>
public sealed record SiteRule(string Domain, bool Allow);

/// <summary>Решает, бесплатен ли сайт, и если нет, сколько стоит. Код покупки — site:домен.</summary>
public sealed class SiteClassifier(SiteCatalog catalog)
{
    public const string KeyPrefix = "site:";

    private readonly HashSet<string> _suffixes = [.. catalog.MultiPartSuffixes];

    public SiteVerdict Classify(string host)
    {
        // Бесплатное побеждает на любой глубине: claude.ai не может стать платным из-за записи о поддомене.
        if (catalog.Free.FirstOrDefault(domain => Domain.IsUnder(host, domain)) is { } free)
        {
            return new SiteVerdict(null, free);
        }

        if (catalog.Sites.Where(site => Domain.IsUnder(host, site.Domain)).MaxBy(site => site.Domain.Length) is { } entry)
        {
            return new SiteVerdict(new LockOffer(KeyPrefix + entry.Domain, entry.Name, entry.Price, LockKind.Site), "каталог");
        }

        var whole = Domain.Registrable(host, _suffixes);
        return new SiteVerdict(new LockOffer(KeyPrefix + whole, whole, catalog.DefaultPrice, LockKind.Site), "цена по умолчанию");
    }

    /// <summary>Предложение по коду покупки. Код приходит из браузера, поэтому цена всегда считается здесь заново.</summary>
    public LockOffer? OfferFor(string key)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        // Покупка зоны вроде com или co.uk открыла бы все сайты в ней.
        var domain = key[KeyPrefix.Length..];
        if (IsZone(domain))
        {
            return null;
        }

        return Domain.IsValid(domain) && Domain.Normalize(domain) == domain && Classify(domain).Offer is { } offer && offer.Key == key
            ? offer
            : null;
    }

    /// <summary>Доменная зона: одно слово без точек (кроме бесплатного вроде localhost) или окончание из нескольких частей.</summary>
    private bool IsZone(string domain) =>
        (!domain.Contains('.') && !domain.StartsWith('[') && !catalog.Free.Contains(domain)) || _suffixes.Contains(domain);

    /// <summary>
    /// Правила для расширения. Всё, что не попало в правила, закрыто.
    /// Закрытые записи нужны, когда открыт сайт целиком, а у поддомена своя цена: yandex.ru куплен, music.yandex.ru нет.
    /// </summary>
    public IReadOnlyList<SiteRule> Rules(IEnumerable<string> unlockedKeys)
    {
        var rules = new Dictionary<string, bool>();
        foreach (var domain in catalog.Free)
        {
            rules[domain] = true;
        }

        var bought = unlockedKeys
            .Where(key => key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            .Select(key => key[KeyPrefix.Length..])
            .Where(domain => Domain.IsValid(domain) && !IsZone(domain))
            .ToHashSet();

        foreach (var site in catalog.Sites)
        {
            if (!catalog.Free.Any(free => Domain.IsUnder(site.Domain, free)))
            {
                rules[site.Domain] = bought.Contains(site.Domain);
            }
        }

        foreach (var domain in bought)
        {
            rules.TryAdd(domain, true);
        }

        return [.. rules.OrderBy(rule => rule.Key, StringComparer.Ordinal).Select(rule => new SiteRule(rule.Key, rule.Value))];
    }

    /// <summary>То же решение, что принимает расширение по правилам. Нужно для проверки их согласованности.</summary>
    public static bool IsAllowed(IReadOnlyList<SiteRule> rules, string host) =>
        rules.Where(rule => Domain.IsUnder(host, rule.Domain)).MaxBy(rule => Domain.Depth(rule.Domain))?.Allow ?? false;
}
