namespace ADHDWindows.Core.Media;

/// <summary>
/// Медиаэлемент страницы: видео или звук. <paramref name="Id"/> постоянен, пока элемент живёт на странице.
/// <paramref name="Position"/> и <paramref name="Duration"/> в секундах, null — неизвестно, бесконечная длительность у прямых эфиров.
/// <paramref name="Playing"/> — не на паузе, не перематывается и не закончился.
/// <paramref name="Audible"/> — звук не выключен в плеере и громкость больше нуля.
/// </summary>
public sealed record BrowserMediaElement(
    string Id,
    bool IsVideo,
    double? Position,
    double? Duration,
    bool Playing,
    double Rate,
    bool Audible,
    string? Title);

/// <summary>
/// Вкладка с медиа. <paramref name="Focused"/> — вкладка выбрана в окне браузера, и это окно в фокусе у браузера.
/// </summary>
public sealed record BrowserTabMedia(int TabId, string Url, bool Focused, IReadOnlyList<BrowserMediaElement> Media);

/// <summary>
/// Отчёт расширения о воспроизведении во всех вкладках браузера. Приходит не в потоке интерфейса.
/// <paramref name="PeerId"/> — подключение браузера, <paramref name="BrowserProcessId"/> — его главный процесс, если известен.
/// Пустой список вкладок и <paramref name="Disconnected"/> — браузер отключился.
/// </summary>
public sealed record BrowserMediaReported(int PeerId, int? BrowserProcessId, IReadOnlyList<BrowserTabMedia> Tabs, bool Disconnected = false);

/// <summary>Установленная игра. <paramref name="Folder"/> — её папка, все файлы внутри принадлежат игре.</summary>
public sealed record GameInstall(string Key, string Name, string Folder);

/// <summary>Модулю нужен список установленных игр. Владелец списка отвечает <see cref="InstalledGamesChanged"/>.</summary>
public sealed record InstalledGamesRequested;

/// <summary>Список установленных игр изменился или был запрошен.</summary>
public sealed record InstalledGamesChanged(IReadOnlyList<GameInstall> Games);

/// <summary>Начисление за хронометраж приостановлено, пока игрок не ответит «Я тут». False — вопрос снят.</summary>
public sealed record PresenceCheckChanged(bool Waiting, string Activity);

/// <summary>Игрок ответил «Я тут».</summary>
public sealed record PresenceConfirmed;
