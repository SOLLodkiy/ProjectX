using ADHDWindows.Core.Data;

namespace ADHDWindows.Modules.Locks.Games;

/// <summary>Игра конкретного лаунчера.</summary>
public sealed record GameRef(string Launcher, string Id)
{
    public bool Matches(InstalledGame game) =>
        Launcher.Equals(game.Launcher, StringComparison.OrdinalIgnoreCase)
        && Id.Equals(game.GameId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Цена игры вне каталога по размеру. Последняя ступень без границы подходит ко всему остальному.</summary>
public sealed record SizePrice(double? UpToGb, long Price);

/// <summary>Одна игра может продаваться в нескольких лаунчерах, цена у неё одна.</summary>
public sealed record GameEntry(string Id, string Name, long Price, IReadOnlyList<GameRef> Match);

/// <summary>Каталог игр из data/locks/games.json.</summary>
public sealed record GameCatalog(
    long DefaultPrice,
    IReadOnlyList<SizePrice> SizePrices,
    IReadOnlyList<GameRef> Ignore,
    IReadOnlyList<GameEntry> Games)
{
    public const string DataKey = "locks/games";

    private const double BytesInGb = 1024d * 1024 * 1024;

    public static GameCatalog Load(GameData data)
    {
        if (!data.TryGet<GameCatalog>(DataKey, out var catalog) || catalog is null)
        {
            throw new InvalidDataException($"Нет каталога {DataKey}");
        }

        catalog.Validate();
        return catalog;
    }

    public void Validate()
    {
        var ids = Games.Select(entry => entry.Id).ToList();
        if (ids.Count != ids.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new InvalidDataException("В каталоге игр есть одинаковые коды");
        }

        var refs = Games.SelectMany(entry => entry.Match).Concat(Ignore).ToList();
        if (refs.Count != refs.Distinct().Count() || refs.Any(item => string.IsNullOrWhiteSpace(item.Launcher) || string.IsNullOrWhiteSpace(item.Id)))
        {
            throw new InvalidDataException("В каталоге игр есть пустая или повторная ссылка на игру");
        }

        // Бесплатных игр нет: требование пользователя.
        if (DefaultPrice <= 0 || Games.Any(entry => entry.Price <= 0) || SizePrices.Any(step => step.Price <= 0))
        {
            throw new InvalidDataException("В каталоге игр есть цена меньше единицы");
        }

        if (SizePrices.Count == 0 || SizePrices[^1].UpToGb is not null
            || SizePrices.SkipLast(1).Any(step => step.UpToGb is null)
            || !SizePrices.SkipLast(1).Select(step => step.UpToGb!.Value).SequenceEqual(SizePrices.SkipLast(1).Select(step => step.UpToGb!.Value).Order()))
        {
            throw new InvalidDataException("Ступени цены по размеру должны расти и заканчиваться ступенью без границы");
        }
    }

    public bool IsIgnored(InstalledGame game) => Ignore.Any(item => item.Matches(game));

    public GameEntry? Find(InstalledGame game) => Games.FirstOrDefault(entry => entry.Match.Any(item => item.Matches(game)));

    /// <summary>Цена по каталогу, иначе по размеру, иначе цена по умолчанию.</summary>
    public long PriceOf(InstalledGame game, long? size)
    {
        if (Find(game) is { } entry)
        {
            return entry.Price;
        }

        if (size is not { } bytes)
        {
            return DefaultPrice;
        }

        var gb = bytes / BytesInGb;
        return SizePrices.First(step => step.UpToGb is null || gb < step.UpToGb).Price;
    }
}
