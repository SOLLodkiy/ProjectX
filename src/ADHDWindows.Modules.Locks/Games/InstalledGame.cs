namespace ADHDWindows.Modules.Locks.Games;

/// <summary>Установленная игра из библиотеки лаунчера.</summary>
/// <param name="Launcher">Код лаунчера: steam, epic, gog, ubisoft.</param>
/// <param name="GameId">Код игры внутри лаунчера. Не меняется при переустановке и обновлении.</param>
/// <param name="Folder">Папка игры. Всё, что запускается из неё, считается этой игрой.</param>
/// <param name="Size">Размер на диске из данных лаунчера, если он там есть.</param>
public sealed record InstalledGame(string Launcher, string GameId, string Name, string Folder, long? Size = null)
{
    /// <summary>Постоянный код покупки. Отличается от кода лаунчера, поэтому покупка лаунчера не открывает игру.</summary>
    public string Key => $"game:{Launcher}:{GameId.ToLowerInvariant()}";
}

/// <summary>Источник игр одного лаунчера.</summary>
public interface IGameSource
{
    string Launcher { get; }

    /// <summary>Игры, которые лаунчер считает установленными. Ошибки чтения не выбрасываются.</summary>
    IReadOnlyList<InstalledGame> Read();

    /// <summary>
    /// Папки, где каждая вложенная папка — игра, например steamapps\common.
    /// Игра без записи лаунчера всё равно опознаётся по такой папке.
    /// </summary>
    IReadOnlyList<string> LibraryRoots();
}
