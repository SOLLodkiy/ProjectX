using ADHDWindows.Core.Locks;
using ADHDWindows.Modules.Locks.Sites;

namespace ADHDWindows.Modules.Locks;

/// <summary>Собирает коллекцию купленного для статистики: покупки из книги и размер каталога по видам.</summary>
public static class CollectionReport
{
    /// <param name="gameKeys">Коды установленных игр: каталог игр — то, что есть на этом компьютере.</param>
    public static CollectionChanged Build(
        IReadOnlyDictionary<string, UnlockRecord> unlocked,
        ProgramCatalog programs,
        SiteCatalog sites,
        IEnumerable<string> gameKeys)
    {
        var items = unlocked
            .Select(pair => new CollectionItem(pair.Key, pair.Value.Name, pair.Value.Kind, pair.Value.Price, pair.Value.When))
            .OrderByDescending(item => item.When)
            .ToList();

        var catalogs = new Dictionary<LockKind, HashSet<string>>
        {
            [LockKind.Program] = [.. programs.Programs.Select(entry => $"program:{entry.Id}")],
            [LockKind.Game] = [.. gameKeys],
            [LockKind.Site] = [.. sites.Sites.Select(site => SiteClassifier.KeyPrefix + site.Domain)],
        };

        // Купленное вне каталога, например программа по цене по умолчанию, тоже часть открытого.
        foreach (var item in items)
        {
            catalogs[item.Kind].Add(item.Key);
        }

        var catalog = catalogs
            .Select(pair => new CollectionCatalog(pair.Key, pair.Value.Count, pair.Value.Count(unlocked.ContainsKey)))
            .ToList();
        return new CollectionChanged(items, catalog);
    }
}
