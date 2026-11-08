using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Logging;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Locks.Sites;

/// <summary>Что наблюдение за браузерами просит у Windows сверх движка блокировки. В тестах подменяется.</summary>
public interface IBrowserSystem
{
    string Title(IntPtr window);

    string? CommandLine(ProcessStamp process);

    string? ImagePath(ProcessStamp process);

    void Launch(string path, IReadOnlyList<string> arguments);
}

/// <summary>
/// Сворачивает окна браузеров, за которыми не следит расширение: без расширения сайты не блокируются.
/// Окно инкогнито опознаётся так: расширению запрещено инкогнито, и заголовок окна не совпал ни с одной вкладкой,
/// о которых оно сообщило. Заголовки меняются не одновременно, поэтому окно сворачивается, только если расхождение держится.
/// </summary>
/// <param name="onlyProfile">Если задано, следить только за браузерами, в командной строке которых есть этот текст. Нужно для проверок.</param>
public sealed class BrowserWatch(
    SiteCatalog catalog,
    IProgramSystem system,
    IBrowserSystem browsers,
    IEventBus events,
    ILog log,
    TimeProvider clock,
    string? onlyProfile = null)
{
    /// <summary>Сколько ждать подключения расширения после запуска браузера или программы.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);

    /// <summary>Сколько должно держаться расхождение заголовка, чтобы окно сочлось окном инкогнито.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    /// <summary>Сколько осмотров подряд должно подтвердить расхождение.</summary>
    public const int SettleChecks = 3;

    /// <summary>Сколько не сворачивать браузер после просьбы открыть страницу расширений.</summary>
    public static readonly TimeSpan SetupTime = TimeSpan.FromMinutes(3);

    private const int CacheLimit = 2048;

    private readonly DateTimeOffset _started = clock.GetUtcNow();
    private readonly Dictionary<ProcessStamp, BrowserEntry?> _browsers = [];
    private readonly Dictionary<ProcessStamp, DateTimeOffset> _firstSeen = [];
    private readonly Dictionary<IntPtr, (DateTimeOffset Since, int Seen)> _suspects = [];
    private readonly Dictionary<string, DateTimeOffset> _setupUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IntPtr> _minimized = [];
    private readonly HashSet<IntPtr> _reported = [];
    private IReadOnlyList<ExtensionProblem> _problems = [];

    public IReadOnlyList<ExtensionProblem> Problems => _problems;

    /// <summary>Осматривает окна браузеров и сворачивает те, что остались без расширения.</summary>
    public void Check(IReadOnlyCollection<BrowserSession> sessions)
    {
        var now = clock.GetUtcNow();
        var problems = new List<ExtensionProblem>();
        var suspects = new HashSet<IntPtr>();
        var newlyMinimized = false;
        foreach (var window in system.Windows())
        {
            if (system.Stamp(window.ProcessId) is not { } process || Identify(process) is not { } browser)
            {
                continue;
            }

            var kind = Judge(window, process, browser, sessions, now, suspects);
            if (kind is not { } problem)
            {
                continue;
            }

            if (!problems.Any(item => item.BrowserId == browser.Id && item.Kind == problem))
            {
                problems.Add(new ExtensionProblem(browser.Id, browser.Name, problem));
            }

            if (!window.IsMinimized)
            {
                system.ForceMinimize(window.Handle);
                _minimized.Add(window.Handle);
                newlyMinimized = true;
            }
        }

        foreach (var stale in _suspects.Keys.Where(handle => !suspects.Contains(handle)).ToList())
        {
            _suspects.Remove(stale);
        }

        Publish(problems, newlyMinimized);
        if (_browsers.Count > CacheLimit)
        {
            _browsers.Clear();
            _firstSeen.Clear();
        }
    }

    /// <summary>Открывает страницу расширений браузера и ненадолго перестаёт его сворачивать.</summary>
    public void OpenExtensionsPage(string browserId)
    {
        if (catalog.Browsers.FirstOrDefault(entry => string.Equals(entry.Id, browserId, StringComparison.OrdinalIgnoreCase)) is not { } browser)
        {
            return;
        }

        _setupUntil[browser.Id] = clock.GetUtcNow() + SetupTime;
        var running = _browsers.Where(pair => pair.Value?.Id == browser.Id).Select(pair => pair.Key).FirstOrDefault(system.IsAlive);
        if (running != default && browsers.ImagePath(running) is { } path)
        {
            foreach (var handle in _minimized)
            {
                system.Restore(handle);
            }

            _minimized.Clear();
            browsers.Launch(path, [.. ProfileArguments(browsers.CommandLine(running)), browser.ExtensionsPage]);
            log.Info($"Открыта страница расширений: {browser.Name}");
        }
    }

    /// <summary>Возвращает свёрнутые окна: программа закрывается или аварийно остановлена.</summary>
    public void ReleaseAll()
    {
        foreach (var handle in _minimized)
        {
            system.Restore(handle);
        }

        _minimized.Clear();
        Publish([], false);
    }

    private ExtensionProblemKind? Judge(
        WindowInfo window,
        ProcessStamp process,
        BrowserEntry browser,
        IReadOnlyCollection<BrowserSession> sessions,
        DateTimeOffset now,
        HashSet<IntPtr> suspects)
    {
        if (!_firstSeen.TryGetValue(process, out var seen))
        {
            seen = now;
            _firstSeen[process] = now;
        }

        var title = browsers.Title(window.Handle);
        if (title.Length == 0
            || catalog.ExtensionPageTitles.Any(word => title.Contains(word, StringComparison.OrdinalIgnoreCase))
            || (_setupUntil.TryGetValue(browser.Id, out var until) && now < until))
        {
            return null;
        }

        var session = sessions.FirstOrDefault(item => item.Ready && item.Browser == process);
        if (session is null)
        {
            var since = seen > _started ? seen : _started;
            return now - since >= Grace ? ExtensionProblemKind.Missing : null;
        }

        if (session.IncognitoAllowed || session.Titles.Any(tab => title.Contains(tab, StringComparison.Ordinal)))
        {
            return null;
        }

        suspects.Add(window.Handle);
        var suspected = _suspects.TryGetValue(window.Handle, out var known) ? (known.Since, Seen: known.Seen + 1) : (Since: now, Seen: 1);
        _suspects[window.Handle] = suspected;
        if (now - suspected.Since < Settle || suspected.Seen < SettleChecks)
        {
            return null;
        }

        if (_reported.Add(window.Handle))
        {
            log.Info($"Окно без вкладки расширения: «{title}», вкладок в отчёте: {session.Titles.Count}");
        }

        return ExtensionProblemKind.Incognito;
    }

    private BrowserEntry? Identify(ProcessStamp process)
    {
        if (!_browsers.TryGetValue(process, out var browser))
        {
            browser = system.ReadFile(process) is { } file
                ? catalog.Browsers.FirstOrDefault(entry => entry.Match.Any(match => match.Matches(file)))
                : null;

            if (browser is not null && onlyProfile is not null
                && browsers.CommandLine(process)?.Contains(onlyProfile, StringComparison.OrdinalIgnoreCase) != true)
            {
                browser = null;
            }

            _browsers[process] = browser;
        }

        return browser;
    }

    /// <summary>Профиль запущенного браузера: без него страница расширений откроется в профиле по умолчанию.</summary>
    public static IEnumerable<string> ProfileArguments(string? commandLine)
    {
        if (commandLine is null)
        {
            yield break;
        }

        foreach (var name in new[] { "--user-data-dir=", "-profile " })
        {
            var start = commandLine.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                continue;
            }

            var value = commandLine[(start + name.Length)..];
            if (value.StartsWith('"'))
            {
                var end = value.IndexOf('"', 1);
                value = end > 0 ? value[1..end] : value[1..];
            }
            else
            {
                value = value.Split(' ')[0];
            }
            if (name.EndsWith(' '))
            {
                yield return name.Trim();
                yield return value;
            }
            else
            {
                yield return name + value;
            }
        }
    }

    private void Publish(List<ExtensionProblem> problems, bool newlyMinimized)
    {
        var changed = !problems.SequenceEqual(_problems);
        if (changed)
        {
            _problems = problems;
            if (problems.Count > 0)
            {
                log.Warn($"Браузер без расширения: {string.Join(", ", problems.Select(problem => $"{problem.BrowserName} ({problem.Kind})"))}");
            }

            events.Publish(new ExtensionProblemsChanged(problems));
        }

        if (problems.Count > 0 && (changed || newlyMinimized))
        {
            events.Publish(new ExtensionScreenRequested());
        }
    }
}
