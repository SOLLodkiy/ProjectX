using System.Text.Json.Nodes;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Quests;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Locks.Sites;

/// <summary>Браузер с подключённым расширением.</summary>
public sealed class BrowserSession(IBrowserPeer peer)
{
    public IBrowserPeer Peer => peer;

    /// <summary>Главный процесс браузера: ему принадлежат его окна.</summary>
    public ProcessStamp? Browser { get; set; }

    /// <summary>Расширение представилось и получило правила.</summary>
    public bool Ready { get; set; }

    public bool IncognitoAllowed { get; set; }

    /// <summary>Заголовки активных вкладок всех окон, которые видит расширение.</summary>
    public IReadOnlyList<string> Titles { get; set; } = [];
}

/// <summary>
/// Блокировка сайтов: отвечает расширениям браузеров. Все методы вызываются из рабочего потока модуля.
/// Сообщения расширения: hello, windows, media, page, offer, unlock. Отчёты media и page уходят в шину для хронометража и заданий.
/// Ответ на запрос несёт тот же id. Страницы заданий дают пропуски (passes) и наблюдение (watch) в сообщении state.
/// </summary>
public sealed class SiteLocks(
    SiteClassifier classifier,
    UnlockLedger ledger,
    IEconomy economy,
    IEventBus events,
    ILog log,
    TimeProvider clock)
{
    private const int MaxTitles = 200;
    private const int MaxTitleLength = 1024;

    /// <summary>Длина выделенного текста в отчёте page.</summary>
    public const int MaxSelection = 200;

    private readonly Dictionary<int, BrowserSession> _sessions = [];
    private Dictionary<string, PageWatch> _pages = new(StringComparer.Ordinal);

    public IReadOnlyCollection<BrowserSession> Sessions => _sessions.Values;

    /// <summary>
    /// Страницы текущих заданий. Несколько заданий на одной странице объединяются.
    /// Если пропуски или наблюдение изменились, новое state уходит всем готовым браузерам.
    /// </summary>
    public void SetQuestPages(IReadOnlyList<QuestPage> pages)
    {
        var merged = new Dictionary<string, PageWatch>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            if (string.IsNullOrEmpty(page.Page))
            {
                continue;
            }

            merged[page.Page] = merged.TryGetValue(page.Page, out var known)
                ? new PageWatch(known.Pass || page.Pass, known.Select || page.WatchSelection, known.Scroll || page.WatchScroll)
                : new PageWatch(page.Pass, page.WatchSelection, page.WatchScroll);
        }

        if (merged.Count == _pages.Count && merged.All(pair => _pages.TryGetValue(pair.Key, out var old) && old == pair.Value))
        {
            return;
        }

        _pages = merged;
        log.Info($"Страницы заданий: {merged.Count}, пропусков {merged.Values.Count(watch => watch.Pass)}");
        Broadcast();
    }

    /// <param name="message">Null — браузер отключился.</param>
    public void OnMessage(IBrowserPeer peer, JsonObject? message)
    {
        if (message is null)
        {
            if (_sessions.Remove(peer.Id, out var gone))
            {
                log.Info($"Расширение браузера отключилось: {peer.Id}");
                events.Publish(new BrowserMediaReported(peer.Id, gone.Browser?.Id, [], Disconnected: true));
            }

            return;
        }

        if (!_sessions.TryGetValue(peer.Id, out var session))
        {
            session = new BrowserSession(peer);
            _sessions[peer.Id] = session;
        }

        switch (Text(message["type"]))
        {
            case "host":
                if (Number(message["browserPid"]) is { } id && Number(message["browserStart"]) is { } start)
                {
                    session.Browser = new ProcessStamp((int)id, start);
                }

                break;
            case "hello":
                session.Ready = true;
                session.IncognitoAllowed = message["incognito"] is JsonValue incognito && incognito.TryGetValue<bool>(out var allowed) && allowed;
                log.Info($"Расширение подключилось: {Text(message["browser"]) ?? "?"} {Text(message["version"]) ?? "?"}, "
                    + $"процесс {session.Browser?.Id.ToString() ?? "?"}, инкогнито {(session.IncognitoAllowed ? "разрешено" : "запрещено")}");
                peer.Send(State());
                break;
            case "windows":
                session.Titles = message["titles"] is JsonArray titles
                    ? [.. titles.Select(Text).OfType<string>().Take(MaxTitles).Select(title => title.Length > MaxTitleLength ? title[..MaxTitleLength] : title)]
                    : [];
                break;
            case "media":
                events.Publish(MediaReport.Parse(peer.Id, session.Browser?.Id, message));
                break;
            case "page":
                if (Page(peer.Id, message) is { } report)
                {
                    events.Publish(report);
                }

                break;
            case "offer":
                peer.Send(Reply(message, Offer(Text(message["url"]))));
                break;
            case "unlock":
                peer.Send(Reply(message, Unlock(Text(message["key"]))));
                break;
        }
    }

    /// <summary>Правила для расширения: что открыто, что закрыто. Всё остальное закрыто.</summary>
    public JsonObject State() => new()
    {
        ["type"] = "state",
        ["rules"] = new JsonArray([.. classifier.Rules(ledger.Keys).Select(rule => new JsonObject
        {
            ["domain"] = rule.Domain,
            ["allow"] = rule.Allow,
        })]),
        ["passes"] = new JsonArray([.. _pages.Where(pair => pair.Value.Pass).Select(pair => (JsonNode?)JsonValue.Create(pair.Key))]),
        ["watch"] = new JsonArray([.. _pages.Select(pair => new JsonObject
        {
            ["page"] = pair.Key,
            ["select"] = pair.Value.Select,
            ["scroll"] = pair.Value.Scroll,
        })]),
    };

    /// <summary>
    /// Отчёт о странице задания. Код страницы считается заново из адреса: браузеру не доверяем.
    /// Неверное сообщение и страницы вне наблюдения дают null.
    /// </summary>
    private BrowserPageReported? Page(int peerId, JsonObject message)
    {
        if (Number(message["tabId"]) is not { } tabId || tabId is < int.MinValue or > int.MaxValue
            || Text(message["url"]) is not { } url
            || PageAddress.Normalize(url) is not { } page
            || !_pages.TryGetValue(page, out var watch)
            || message["focused"] is not JsonValue focusedValue || !focusedValue.TryGetValue<bool>(out var focused))
        {
            return null;
        }

        return Text(message["event"]) switch
        {
            "visit" => new BrowserPageReported(peerId, (int)tabId, page, url, PageActivityKind.Visit, focused),
            "select" when watch.Select && Text(message["text"]) is { } text =>
                new BrowserPageReported(peerId, (int)tabId, page, url, PageActivityKind.Selection, focused, Cut(text, MaxSelection)),
            "scroll" when watch.Scroll && Fraction(message["share"]) is { } share =>
                new BrowserPageReported(peerId, (int)tabId, page, url, PageActivityKind.Scroll, focused, Share: Math.Clamp(share, 0, 1)),
            _ => null,
        };
    }

    private JsonObject Offer(string? url)
    {
        // Пропуск задания открывает только свою страницу. Остальные страницы сайта продаются как раньше.
        if (PageAddress.Normalize(url) is { } page && _pages.TryGetValue(page, out var watch) && watch.Pass)
        {
            return new JsonObject { ["free"] = true };
        }

        if (Domain.FromUrl(url) is not { } host
            || classifier.Classify(host).Offer is not { } offer
            || ledger.Contains(offer.Key))
        {
            return new JsonObject { ["free"] = true };
        }

        return new JsonObject
        {
            ["key"] = offer.Key,
            ["name"] = offer.Name,
            ["price"] = offer.Price,
            ["balance"] = economy.Balance,
        };
    }

    private JsonObject Unlock(string? key)
    {
        if (key is null || classifier.OfferFor(key) is not { } offer)
        {
            return new JsonObject { ["error"] = "unknown" };
        }

        if (ledger.Contains(offer.Key))
        {
            return new JsonObject { ["unlocked"] = true };
        }

        if (!economy.TrySpend(offer.Price, $"unlock:{offer.Key}"))
        {
            events.Publish(new UnlockDenied(offer.Key, offer.Price));
            return new JsonObject { ["denied"] = true, ["price"] = offer.Price, ["balance"] = economy.Balance };
        }

        var when = clock.GetUtcNow();
        ledger.Add(offer, when);
        log.Info($"Разблокирован сайт: {offer.Name} за {offer.Price}");
        events.Publish(new ItemUnlocked(offer, when));

        // Новые правила уходят раньше ответа: страница покупки откроет сайт, когда браузер уже знает о покупке.
        Broadcast();
        return new JsonObject { ["unlocked"] = true };
    }

    private void Broadcast()
    {
        var state = State();
        foreach (var other in _sessions.Values.Where(other => other.Ready))
        {
            other.Peer.Send(state.DeepClone().AsObject());
        }
    }

    private static string Cut(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        // Половина суррогатной пары на конце дала бы неверную строку.
        return char.IsHighSurrogate(text[length - 1]) ? text[..(length - 1)] : text[..length];
    }

    private static double? Fraction(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number)
            ? number
            : null;

    private sealed record PageWatch(bool Pass, bool Select, bool Scroll);

    private static JsonObject Reply(JsonObject request, JsonObject answer)
    {
        answer["type"] = "reply";
        answer["id"] = request["id"]?.DeepClone();
        return answer;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}
