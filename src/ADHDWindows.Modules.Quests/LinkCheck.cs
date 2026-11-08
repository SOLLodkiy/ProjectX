using System.Net;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Modules.Quests;

/// <summary>Проверяет, открывается ли ссылка задания.</summary>
public interface ILinkChecker
{
    /// <summary>True — страница отвечает успехом. Исключение считается неудачей.</summary>
    Task<bool> IsOpenAsync(string url, CancellationToken cancellation);
}

/// <summary>
/// Проверка через HTTP: GET, перенаправления разрешены, успех — код 2xx в конце цепочки, ожидание до 10 секунд.
/// Запрос не несёт личных данных: общий User-Agent программы, без куки.
/// </summary>
public sealed class HttpLinkChecker : ILinkChecker, IDisposable
{
    public const string UserAgent = "ADHDWindows/1.0 (quest link check)";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client;

    public HttpLinkChecker()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _client = new HttpClient(handler) { Timeout = Timeout };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public async Task<bool> IsOpenAsync(string url, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Без проверки: все ссылки открываются. Для доски без сети.</summary>
public sealed class OpenLinkChecker : ILinkChecker
{
    public static OpenLinkChecker Instance { get; } = new();

    public Task<bool> IsOpenAsync(string url, CancellationToken cancellation) => Task.FromResult(true);
}

/// <summary>
/// Результаты проверки ссылок в памяти: удачная помнится 6 часов, неудачная — 30 минут.
/// Ссылки на этот компьютер (localhost, 127.0.0.1, [::1]) по сети не проверяются и считаются открытыми.
/// Проверка идёт в фоне, одна ссылка не проверяется дважды одновременно.
/// </summary>
public sealed class LinkStatus : IDisposable
{
    public static readonly TimeSpan OpenFor = TimeSpan.FromHours(6);
    public static readonly TimeSpan BrokenFor = TimeSpan.FromMinutes(30);

    private readonly ILinkChecker _checker;
    private readonly TimeProvider _clock;
    private readonly ILog _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (bool Open, DateTimeOffset Checked)> _results = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();

    public LinkStatus(ILinkChecker checker, TimeProvider clock, ILog log)
    {
        _checker = checker;
        _clock = clock;
        _log = log;
    }

    /// <summary>Проверка закончилась: ссылка и открывается ли она. Приходит не под замком, из любого потока.</summary>
    public event Action<string, bool>? Checked;

    /// <summary>Ссылка проверена недавно и открывается.</summary>
    public bool IsOpen(string url)
    {
        if (IsLocal(url))
        {
            return true;
        }

        lock (_lock)
        {
            return _results.TryGetValue(url, out var result) && result.Open && _clock.GetUtcNow() - result.Checked < OpenFor;
        }
    }

    /// <summary>Запускает фоновую проверку ссылок без свежего результата.</summary>
    public void Ensure(IEnumerable<string> urls)
    {
        var start = new List<string>();
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            foreach (var url in urls)
            {
                if (IsLocal(url) || _running.Contains(url))
                {
                    continue;
                }

                if (_results.TryGetValue(url, out var result) && now - result.Checked < (result.Open ? OpenFor : BrokenFor))
                {
                    continue;
                }

                _running.Add(url);
                start.Add(url);
            }
        }

        foreach (var url in start)
        {
            _ = RunAsync(url);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>Адрес этого компьютера: проверки в браузере и тестовые страницы.</summary>
    public static bool IsLocal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));

    private async Task RunAsync(string url)
    {
        bool open;
        try
        {
            open = await _checker.IsOpenAsync(url, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            open = false;
        }

        lock (_lock)
        {
            _results[url] = (open, _clock.GetUtcNow());
            _running.Remove(url);
        }

        if (open)
        {
            _log.Info($"Ссылка задания открывается: {url}");
        }
        else
        {
            _log.Warn($"Ссылка задания не открывается, задание не выдаётся: {url}");
        }

        Checked?.Invoke(url, open);
    }
}
