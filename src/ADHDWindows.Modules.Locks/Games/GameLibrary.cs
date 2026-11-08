using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Modules.Locks.Games;

/// <summary>
/// Все установленные игры и их папки. Игра опознаётся по папке, поэтому блокируется и при запуске
/// через лаунчер, и при запуске файла напрямую. Вызывается из одного рабочего потока.
/// </summary>
public sealed class GameLibrary(IReadOnlyList<IGameSource> sources, GameCatalog catalog, ILog log, TimeProvider clock)
{
    /// <summary>Как часто можно перечитывать библиотеки вне расписания, когда запущено неизвестное из папки библиотеки.</summary>
    private static readonly TimeSpan ForcedRefreshPause = TimeSpan.FromSeconds(5);

    // Игра не может занимать такие папки целиком: тогда «игрой» стало бы всё внутри.
    private static readonly string[] ProtectedFolders =
    [
        .. new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.CommonApplicationData,
        }
        .Select(Environment.GetFolderPath)
        .Where(path => path.Length > 0),
        AppContext.BaseDirectory,
    ];

    private readonly Dictionary<string, long?> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _known = [];
    private List<(string Folder, InstalledGame Game)> _folders = [];
    private List<(string Root, string Launcher)> _roots = [];
    private List<string> _ignored = [];
    private string _signature = string.Empty;
    private DateTimeOffset _lastForced = DateTimeOffset.MinValue;
    private bool _loaded;

    public IReadOnlyList<InstalledGame> Games => [.. _folders.Select(item => item.Game)];

    /// <summary>Перечитывает библиотеки. Возвращает true, если список игр или их папок изменился.</summary>
    public bool Refresh()
    {
        var games = new List<InstalledGame>();
        var roots = new List<(string Root, string Launcher)>();
        foreach (var source in sources)
        {
            try
            {
                games.AddRange(source.Read());
                roots.AddRange(source.LibraryRoots().Select(root => (Normalize(root), source.Launcher)));
            }
            catch (Exception exception)
            {
                // Сломанный лаунчер не должен мешать остальным.
                log.Error($"Не удалось прочитать игры {source.Launcher}", exception);
            }
        }

        // Программы и общие библиотеки Steam лежат в папке библиотеки, но игрой не являются.
        var ignored = games.Where(catalog.IsIgnored).Select(game => Normalize(game.Folder)).ToList();
        var folders = games
            .Where(game => !catalog.IsIgnored(game))
            .Select(game => (Folder: Normalize(game.Folder), Game: game))
            .Where(item => IsGameFolder(item.Folder, roots))
            .DistinctBy(item => item.Folder, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Folder.Length)
            .ToList();

        var signature = string.Join('\n', folders.Select(item => $"{item.Game.Key}={item.Folder}").Order(StringComparer.Ordinal));
        var changed = signature != _signature;
        _folders = folders;
        _roots = [.. roots.OrderByDescending(item => item.Root.Length)];
        _signature = signature;
        _ignored = ignored;

        foreach (var (_, game) in folders.Where(item => _known.Add(item.Game.Key)))
        {
            if (_loaded)
            {
                log.Info($"Новая игра: {game.Name} ({game.Launcher}), цена {Offer(game).Price}");
            }
        }

        if (!_loaded)
        {
            log.Info($"Найдено игр: {folders.Count}");
            _loaded = true;
        }

        return changed;
    }

    /// <summary>Игра, которой принадлежит файл, или null.</summary>
    public InstalledGame? Find(string path)
    {
        var full = Normalize(Path.GetDirectoryName(path) ?? path);
        if (Lookup(full) is { } game)
        {
            return game;
        }

        if (RootOf(full) is null || IsIgnored(full))
        {
            return null;
        }

        // Игра только что установлена, а расписание ещё не дошло до её записи.
        var now = clock.GetUtcNow();
        if (now - _lastForced >= ForcedRefreshPause)
        {
            _lastForced = now;
            Refresh();
            if (Lookup(full) is { } fresh)
            {
                return fresh;
            }
        }

        if (IsIgnored(full))
        {
            return null;
        }

        // Записи лаунчера нет, но папка библиотеки говорит сама за себя.
        var (root, launcher) = RootOf(full)!.Value;
        var name = full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries)[0];
        return new InstalledGame(launcher, $"folder:{name}", name, root + name);
    }

    public LockOffer Offer(InstalledGame game)
    {
        var entry = catalog.Find(game);
        var price = entry is not null ? entry.Price : catalog.PriceOf(game, game.Size ?? SizeOf(game.Folder));
        return new LockOffer(game.Key, entry?.Name ?? game.Name, price, LockKind.Game);
    }

    private bool IsIgnored(string folder) =>
        _ignored.Any(item => folder.StartsWith(item, StringComparison.OrdinalIgnoreCase));

    private InstalledGame? Lookup(string folder) =>
        _folders.FirstOrDefault(item => folder.StartsWith(item.Folder, StringComparison.OrdinalIgnoreCase)).Game;

    private (string Root, string Launcher)? RootOf(string folder)
    {
        foreach (var item in _roots)
        {
            // Сама папка библиотеки — не игра, нужна хотя бы одна вложенная папка.
            if (folder.Length > item.Root.Length && folder.StartsWith(item.Root, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private static bool IsGameFolder(string folder, List<(string Root, string Launcher)> roots)
    {
        // Корень диска.
        if (Path.GetPathRoot(folder)?.Length >= folder.Length)
        {
            return false;
        }

        return !ProtectedFolders.Select(Normalize).Concat(roots.Select(item => item.Root))
            .Any(other => other.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Размер считается один раз за запуск: папка большой игры может содержать сотни тысяч файлов.</summary>
    private long? SizeOf(string folder)
    {
        if (_sizes.TryGetValue(folder, out var cached))
        {
            return cached;
        }

        long? size = null;
        try
        {
            if (Directory.Exists(folder))
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                size = new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(file => file.Length);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Не удалось узнать размер папки {folder}: {exception.Message}");
        }

        _sizes[folder] = size;
        return size;
    }

    /// <summary>Полный путь с обратной чертой на конце: так D:\Game не совпадёт с D:\Game2.</summary>
    private static string Normalize(string folder)
    {
        try
        {
            return Path.GetFullPath(folder.Replace('/', '\\')).TrimEnd('\\') + '\\';
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return folder;
        }
    }
}
