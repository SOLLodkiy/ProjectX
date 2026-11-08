using ADHDWindows.Core.Data;

namespace ADHDWindows.Modules.Locks.Sites;

public sealed record SiteEntry(string Domain, string Name, long Price);

/// <summary>Поддерживаемый браузер. Опознаётся по данным файла, как программы в каталоге программ.</summary>
public sealed record BrowserEntry(string Id, string Name, string ExtensionsPage, IReadOnlyList<FileMatch> Match);

/// <summary>Каталог сайтов из data/locks/sites.json.</summary>
public sealed record SiteCatalog(
    long DefaultPrice,
    IReadOnlyList<string> MultiPartSuffixes,
    IReadOnlyList<SiteEntry> Sites,
    IReadOnlyList<string> Free,
    IReadOnlyList<BrowserEntry> Browsers,
    IReadOnlyList<string> ExtensionPageTitles)
{
    public const string DataKey = "locks/sites";

    /// <summary>Без каталога неизвестно, что бесплатно: claude.ai мог бы оказаться заблокированным.</summary>
    public static SiteCatalog Load(GameData data)
    {
        if (!data.TryGet<SiteCatalog>(DataKey, out var catalog) || catalog is null)
        {
            throw new InvalidDataException($"Нет каталога {DataKey}");
        }

        catalog.Validate();
        return catalog;
    }

    public void Validate()
    {
        if (DefaultPrice <= 0 || Sites.Any(site => site.Price <= 0))
        {
            throw new InvalidDataException("В каталоге сайтов есть цена меньше единицы");
        }

        var domains = Sites.Select(site => site.Domain).Concat(Free).ToList();
        if (domains.Any(domain => !Domain.IsValid(domain) || Domain.Normalize(domain) != domain))
        {
            throw new InvalidDataException("В каталоге сайтов есть неверный домен");
        }

        if (Sites.Select(site => site.Domain).Distinct().Count() != Sites.Count || Free.Distinct().Count() != Free.Count)
        {
            throw new InvalidDataException("В каталоге сайтов есть одинаковые домены");
        }

        if (Browsers.Select(browser => browser.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Browsers.Count
            || Browsers.Any(browser => browser.Match.Count == 0 || browser.Match.Any(match => match.IsEmpty)))
        {
            throw new InvalidDataException("В списке браузеров есть повтор или пустое условие");
        }
    }
}
