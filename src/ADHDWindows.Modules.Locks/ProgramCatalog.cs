using System.Text.RegularExpressions;
using ADHDWindows.Core.Data;

namespace ADHDWindows.Modules.Locks;

/// <summary>Условие опознания. Совпасть должны все заполненные поля, символ * заменяет любой текст.</summary>
public sealed record FileMatch(
    string? Company = null,
    string? Product = null,
    string? Original = null,
    string? Description = null,
    string? Package = null,
    string? Path = null)
{
    public bool IsEmpty => new[] { Company, Product, Original, Description, Package, Path }.All(string.IsNullOrWhiteSpace);

    public bool Matches(ProgramFile file) =>
        Fits(Company, file.Company)
        && Fits(Product, file.Product)
        && Fits(Original, file.Original)
        && Fits(Description, file.Description)
        && Fits(Package, file.Package)
        && Fits(Path is null ? null : Environment.ExpandEnvironmentVariables(Path), file.Path);

    private static bool Fits(string? pattern, string? value)
    {
        if (pattern is null)
        {
            return true;
        }

        if (value is null)
        {
            return false;
        }

        var expression = $"^{Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal)}$";
        return Regex.IsMatch(value, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }
}

public sealed record ProgramEntry(string Id, string Name, long Price, IReadOnlyList<FileMatch> Match);

public sealed record FreeEntry(string Id, IReadOnlyList<FileMatch> Match);

/// <summary>Каталог программ из data/locks/programs.json.</summary>
public sealed record ProgramCatalog(
    long DefaultPrice,
    IReadOnlyList<string> InstallerWords,
    IReadOnlyList<ProgramEntry> Programs,
    IReadOnlyList<FreeEntry> Free)
{
    public const string DataKey = "locks/programs";

    /// <summary>Без каталога неизвестно, что бесплатно, поэтому блокировать нельзя: модуль не запустится.</summary>
    public static ProgramCatalog Load(GameData data)
    {
        if (!data.TryGet<ProgramCatalog>(DataKey, out var catalog) || catalog is null)
        {
            throw new InvalidDataException($"Нет каталога {DataKey}");
        }

        catalog.Validate();
        return catalog;
    }

    public void Validate()
    {
        var ids = Programs.Select(entry => entry.Id).Concat(Free.Select(entry => entry.Id)).ToList();
        if (ids.Count != ids.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new InvalidDataException("В каталоге есть одинаковые коды");
        }

        if (DefaultPrice <= 0 || Programs.Any(entry => entry.Price <= 0))
        {
            throw new InvalidDataException("В каталоге есть цена меньше единицы");
        }

        // Пустое условие подошло бы к любому файлу.
        if (Programs.SelectMany(entry => entry.Match).Concat(Free.SelectMany(entry => entry.Match)).Any(match => match.IsEmpty))
        {
            throw new InvalidDataException("В каталоге есть пустое условие");
        }
    }
}
