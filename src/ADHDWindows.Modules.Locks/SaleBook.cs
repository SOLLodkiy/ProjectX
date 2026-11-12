using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Locks.Sites;

namespace ADHDWindows.Modules.Locks;

/// <summary>Скидка на одну покупку до <paramref name="Until"/>.</summary>
public sealed record SaleRecord(string Key, int Percent, DateTimeOffset Until);

/// <summary>
/// Распродажи (идея 011): временная скидка на сайт или программу из каталога. Скидки хранятся в сохранении и
/// переживают перезапуск. Цена со скидкой действует везде, где показывается и списывается цена. Потокобезопасен.
/// </summary>
public sealed class SaleBook(IKeyValueStore saves, TimeProvider clock)
{
    public const string SaveKey = "sales";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, SaleRecord> _sales = Load(saves);

    /// <summary>Скидки изменились: экрану покупки нужны новые цены.</summary>
    public event Action? Changed;

    /// <summary>Цена со скидкой: не меньше 1 и неровная только от процента.</summary>
    public static long Discounted(long price, int percent) =>
        Math.Max(1, (long)Math.Ceiling(price * (100 - Math.Clamp(percent, 0, 99)) / 100m));

    /// <summary>Та же покупка с ценой со скидкой, если она действует.</summary>
    public LockOffer Apply(LockOffer offer)
    {
        lock (_lock)
        {
            return _sales.TryGetValue(offer.Key, out var sale) && sale.Until > clock.GetUtcNow()
                ? offer with { Price = Discounted(offer.Price, sale.Percent) }
                : offer;
        }
    }

    public IReadOnlyList<SaleRecord> Active
    {
        get
        {
            lock (_lock)
            {
                var now = clock.GetUtcNow();
                return [.. _sales.Values.Where(sale => sale.Until > now)];
            }
        }
    }

    /// <summary>
    /// Выбирает случайную некупленную запись каталога без скидки и уценивает её. Null — уценить нечего.
    /// </summary>
    public LockSaleStarted? Start(
        LockSaleRequested request,
        ProgramCatalog programs,
        SiteCatalog sites,
        Func<string, bool> unlocked,
        Random random)
    {
        LockSaleStarted? started;
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            Expire(now);
            var candidates = programs.Programs
                .Select(program => new LockOffer($"program:{program.Id}", program.Name, program.Price, LockKind.Program))
                .Concat(sites.Sites.Select(site => new LockOffer(SiteClassifier.KeyPrefix + site.Domain, site.Name, site.Price, LockKind.Site)))
                .Where(offer => !unlocked(offer.Key) && !_sales.ContainsKey(offer.Key))
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var offer = candidates[random.Next(candidates.Count)];
            var percent = Math.Clamp(request.Percent, 1, 90);
            var sale = new SaleRecord(offer.Key, percent, now + request.Duration);
            _sales[offer.Key] = sale;
            saves.Set(SaveKey, _sales.Values.ToList());
            started = new LockSaleStarted(request.Requester, offer, Discounted(offer.Price, percent), percent, sale.Until);
        }

        Changed?.Invoke();
        return started;
    }

    /// <summary>Убирает закончившиеся скидки. True — что-то закончилось.</summary>
    public bool Tick()
    {
        bool expired;
        lock (_lock)
        {
            expired = Expire(clock.GetUtcNow());
        }

        if (expired)
        {
            Changed?.Invoke();
        }

        return expired;
    }

    private bool Expire(DateTimeOffset now)
    {
        var gone = _sales.Values.Where(sale => sale.Until <= now).Select(sale => sale.Key).ToList();
        gone.ForEach(key => _sales.Remove(key));
        if (gone.Count > 0)
        {
            saves.Set(SaveKey, _sales.Values.ToList());
        }

        return gone.Count > 0;
    }

    private static Dictionary<string, SaleRecord> Load(IKeyValueStore saves) =>
        (saves.Get<List<SaleRecord>>(SaveKey) ?? [])
            .Where(sale => !string.IsNullOrEmpty(sale.Key))
            .GroupBy(sale => sale.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
}
