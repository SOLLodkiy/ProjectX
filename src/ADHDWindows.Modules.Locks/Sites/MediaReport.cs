using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ADHDWindows.Core.Media;

namespace ADHDWindows.Modules.Locks.Sites;

/// <summary>
/// Разбирает сообщение расширения о воспроизведении. Канал браузера принадлежит блокировке,
/// а отчёт уходит в шину: хронометраж считает модуль, который о блокировке ничего не знает.
/// </summary>
public static class MediaReport
{
    public const int MaxTabs = 64;
    public const int MaxMedia = 16;
    private const int MaxText = 512;

    public static BrowserMediaReported Parse(int peerId, int? browserProcessId, JsonObject message)
    {
        var tabs = new List<BrowserTabMedia>();
        if (message["tabs"] is JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>().Take(MaxTabs))
            {
                if (Number(item["tabId"]) is not { } tabId || Text(item["url"]) is not { } url)
                {
                    continue;
                }

                var media = item["media"] is JsonArray elements
                    ? elements.OfType<JsonObject>().Take(MaxMedia).Select(Element).OfType<BrowserMediaElement>().ToList()
                    : [];
                tabs.Add(new BrowserTabMedia((int)tabId, url, Flag(item["focused"]), media));
            }
        }

        return new BrowserMediaReported(peerId, browserProcessId, tabs);
    }

    private static BrowserMediaElement? Element(JsonObject item)
    {
        if (Text(item["id"]) is not { } id)
        {
            return null;
        }

        var rate = Number(item["rate"]) ?? 1;
        return new BrowserMediaElement(
            id,
            Flag(item["video"]),
            Seconds(item["position"]),
            Seconds(item["duration"]),
            Flag(item["playing"]),
            double.IsFinite(rate) ? Math.Clamp(rate, 0, 16) : 1,
            Flag(item["audible"]),
            Text(item["title"]));
    }

    private static double? Seconds(JsonNode? node) => Number(node) is { } value && double.IsFinite(value) && value >= 0 ? value : null;

    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return null;
        }

        return text.Length > MaxText ? text[..MaxText] : text;
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}
