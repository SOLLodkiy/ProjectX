using ADHDWindows.Core.Locks;
using ADHDWindows.Modules.Locks.Games;

namespace ADHDWindows.Modules.Locks;

/// <summary>Решение о программе. <paramref name="Offer"/> равен null, если она бесплатна.</summary>
public sealed record ProgramVerdict(LockOffer? Offer, string Reason);

/// <summary>Решает, бесплатна ли программа, и если нет, сколько стоит.</summary>
/// <param name="ownFiles">Файлы самой программы.</param>
/// <param name="onlyUnder">Если задано, блокируется только содержимое этой папки. Нужно для проверок.</param>
/// <param name="games">Установленные игры. Игра опознаётся по папке раньше каталога программ.</param>
public sealed class ProgramClassifier(
    ProgramCatalog catalog,
    IEnumerable<string> ownFiles,
    string? onlyUnder = null,
    GameLibrary? games = null)
{
    private readonly HashSet<string> _ownFiles = new(ownFiles, StringComparer.OrdinalIgnoreCase);
    private readonly string? _onlyUnder = onlyUnder is null ? null : Path.GetFullPath(onlyUnder).TrimEnd('\\') + '\\';

    public ProgramVerdict Classify(ProgramFile? file)
    {
        if (file is null)
        {
            return Free("нет доступа к файлу");
        }

        if (_ownFiles.Contains(file.Path))
        {
            return Free("ADHDWindows");
        }

        if (_onlyUnder is not null && !file.Path.StartsWith(_onlyUnder, StringComparison.OrdinalIgnoreCase))
        {
            return Free("вне проверяемой папки");
        }

        if (catalog.Free.FirstOrDefault(entry => Matches(entry.Match, file)) is { } free)
        {
            return Free(free.Id);
        }

        // Установщик и деинсталлятор из папки игры остаются бесплатными, как и любые другие.
        if (games?.Find(file.Path) is { } game && !IsInstaller(file))
        {
            return new ProgramVerdict(games.Offer(game), "игра");
        }

        if (catalog.Programs.FirstOrDefault(entry => Matches(entry.Match, file)) is { } program)
        {
            return new ProgramVerdict(
                new LockOffer($"program:{program.Id}", program.Name, program.Price, LockKind.Program),
                "каталог");
        }

        if (file.InWindowsFolder)
        {
            return Free("папка Windows");
        }

        // Смотрим только внутрь файла: иначе переименование в setup.exe открывало бы любую программу.
        if (IsInstaller(file))
        {
            return Free("установщик");
        }

        return new ProgramVerdict(
            new LockOffer(file.IdentityKey, file.DisplayName, catalog.DefaultPrice, LockKind.Program),
            "цена по умолчанию");
    }

    private bool IsInstaller(ProgramFile file) =>
        catalog.InstallerWords.Any(word =>
            file.Description?.Contains(word, StringComparison.OrdinalIgnoreCase) == true
            || file.Product?.Contains(word, StringComparison.OrdinalIgnoreCase) == true);

    private static bool Matches(IReadOnlyList<FileMatch> matches, ProgramFile file) =>
        matches.Any(match => match.Matches(file));

    private static ProgramVerdict Free(string reason) => new(null, reason);
}
