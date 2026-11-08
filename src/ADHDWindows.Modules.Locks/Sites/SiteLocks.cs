using System.Text.Json.Nodes;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Logging;
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
/// Сообщения расширения: hello, windows, offer, unlock. Ответ на запрос несёт тот же id.
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

    private readonly Dictionary<int, BrowserSession> _sessions = [];

    public IReadOnlyCollection<BrowserSession> Sessions => _sessions.Values;

    /// <param name="message">Null — браузер отключился.</param>
    public void OnMessage(IBrowserPeer peer, JsonObject? message)
    {
        if (message is null)
        {
            if (_sessions.Remove(peer.Id))
            {
                log.Info($"Расширение браузера отключилось: {peer.Id}");
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
    };

    private JsonObject Offer(string? url)
    {
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
        var state = State();
        foreach (var other in _sessions.Values.Where(other => other.Ready))
        {
            other.Peer.Send(state.DeepClone().AsObject());
        }

        return new JsonObject { ["unlocked"] = true };
    }

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
