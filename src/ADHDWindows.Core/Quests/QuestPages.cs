namespace ADHDWindows.Core.Quests;

/// <summary>
/// Страница текущего задания. <paramref name="Page"/> — код страницы из <see cref="Locks.PageAddress.Normalize"/>,
/// <paramref name="Url"/> — исходная ссылка задания.
/// <paramref name="Pass"/> — на время задания страница открыта бесплатно, даже если её сайт не куплен. Пропуск открывает только эту страницу.
/// <paramref name="WatchSelection"/> и <paramref name="WatchScroll"/> — расширение сообщает о выделении текста и прокрутке
/// только на таких страницах, на остальных оно их не читает.
/// </summary>
public sealed record QuestPage(string QuestId, string Page, string Url, bool Pass, bool WatchSelection, bool WatchScroll);

/// <summary>Страницы всех текущих заданий. Владелец заданий публикует при каждой смене и по запросу. Пустой список — пропусков нет.</summary>
public sealed record QuestPagesChanged(IReadOnlyList<QuestPage> Pages);

/// <summary>Модулю нужны страницы заданий, например блокировке после запуска. Владелец заданий отвечает <see cref="QuestPagesChanged"/>.</summary>
public sealed record QuestPagesRequested;

/// <summary>Что произошло на странице задания.</summary>
public enum PageActivityKind
{
    /// <summary>Страница открыта и загружена.</summary>
    Visit,

    /// <summary>Игрок выделил текст, он в <see cref="BrowserPageReported.Text"/>.</summary>
    Selection,

    /// <summary>Страница прокручена, доля от 0 до 1 в <see cref="BrowserPageReported.Share"/>.</summary>
    Scroll,
}

/// <summary>
/// Расширение браузера сообщило о действии на странице задания. Приходит не в потоке интерфейса.
/// <paramref name="Page"/> — код страницы, <paramref name="Url"/> — полный адрес вкладки.
/// <paramref name="Focused"/> — вкладка выбрана в окне браузера, которое в фокусе.
/// <paramref name="Text"/> — выделенный текст, не длиннее 200 знаков. <paramref name="Share"/> — докуда прокручена страница.
/// </summary>
public sealed record BrowserPageReported(
    int PeerId,
    int TabId,
    string Page,
    string Url,
    PageActivityKind Kind,
    bool Focused,
    string? Text = null,
    double? Share = null);

/// <summary>
/// Подряд выполнено <paramref name="Length"/> заданий, цепочка закрыта и начинается заново.
/// Награду за цепочку (сундук, идея 011) выдаёт механика сундуков.
/// </summary>
public sealed record QuestChainCompleted(int Length);
