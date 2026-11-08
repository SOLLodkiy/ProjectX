using System.Text.Json.Nodes;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Locks;
using ADHDWindows.Modules.Locks.Sites;
using Xunit;

namespace ADHDWindows.Tests;

public sealed class QuestPageLockTests : IDisposable
{
    private const string Cucumber = "ru.wikipedia.org/wiki/Огурец";
    private const string Track = "soundcloud.com/artist/track-name";

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly EventBus _bus;
    private readonly CoinEconomy _economy;
    private readonly SiteLocks _sites;
    private readonly SiteLockTests.FakePeer _peer = new(1);
    private readonly List<BrowserPageReported> _reports = [];

    public QuestPageLockTests()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        _bus = new EventBus(_log);
        var store = new JsonFileStore(_temp.Combine("save.json"), _log);
        _economy = new CoinEconomy(store, _bus, TimeProvider.System);
        _sites = new SiteLocks(new SiteClassifier(SiteCatalog.Load(data)), new UnlockLedger(store), _economy, _bus, _log, TimeProvider.System);
        _bus.Subscribe<BrowserPageReported>(_reports.Add);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void State_has_empty_passes_and_watch_without_quests()
    {
        var state = _sites.State();

        Assert.Empty(state["passes"]!.AsArray());
        Assert.Empty(state["watch"]!.AsArray());
    }

    [Fact]
    public void State_lists_passes_and_watched_pages()
    {
        _sites.SetQuestPages(
        [
            new QuestPage("read", Cucumber, "https://ru.wikipedia.org/wiki/Огурец", Pass: true, WatchSelection: true, WatchScroll: true),
            new QuestPage("listen", Track, "https://soundcloud.com/artist/track-name", Pass: false, WatchSelection: false, WatchScroll: false),
        ]);

        var state = _sites.State();

        Assert.Equal([Cucumber], state["passes"]!.AsArray().Select(node => (string?)node));
        var watch = state["watch"]!.AsArray().Select(node => node!.AsObject()).ToDictionary(node => (string)node["page"]!);
        Assert.Equal([Cucumber, Track], watch.Keys.Order(StringComparer.Ordinal));
        Assert.True((bool)watch[Cucumber]["select"]!);
        Assert.True((bool)watch[Cucumber]["scroll"]!);
        Assert.False((bool)watch[Track]["select"]!);
        Assert.False((bool)watch[Track]["scroll"]!);
    }

    [Fact]
    public void Several_quests_on_one_page_are_merged()
    {
        _sites.SetQuestPages(
        [
            new QuestPage("a", Cucumber, "https://ru.wikipedia.org/wiki/Огурец", Pass: false, WatchSelection: true, WatchScroll: false),
            new QuestPage("b", Cucumber, "https://ru.wikipedia.org/wiki/Огурец", Pass: true, WatchSelection: false, WatchScroll: true),
        ]);

        var state = _sites.State();

        Assert.Equal([Cucumber], state["passes"]!.AsArray().Select(node => (string?)node));
        var watch = Assert.Single(state["watch"]!.AsArray())!;
        Assert.True((bool)watch["select"]!);
        Assert.True((bool)watch["scroll"]!);
    }

    [Fact]
    public void Changed_pages_are_sent_to_ready_browsers_only_once()
    {
        var waiting = new SiteLockTests.FakePeer(2);
        Send(new JsonObject { ["type"] = "hello" });
        _sites.OnMessage(waiting, new JsonObject { ["type"] = "host", ["browserPid"] = 5, ["browserStart"] = 1 });
        _peer.Sent.Clear();

        var pages = new[] { PassPage() };
        _sites.SetQuestPages(pages);
        _sites.SetQuestPages([PassPage()]);

        var state = Assert.Single(_peer.Sent);
        Assert.Equal("state", (string?)state["type"]);
        Assert.Equal([Cucumber], state["passes"]!.AsArray().Select(node => (string?)node));
        Assert.Contains(state["rules"]!.AsArray(), rule => (string?)rule!["domain"] == "claude.ai");
        Assert.Empty(waiting.Sent);
    }

    [Fact]
    public void Removed_quest_takes_the_pass_away()
    {
        Send(new JsonObject { ["type"] = "hello" });
        _sites.SetQuestPages([PassPage()]);
        _peer.Sent.Clear();

        _sites.SetQuestPages([]);

        var state = Assert.Single(_peer.Sent);
        Assert.Empty(state["passes"]!.AsArray());
        Assert.Empty(state["watch"]!.AsArray());
        Send(Offer(1, "https://ru.wikipedia.org/wiki/Огурец"));
        Assert.Null(_peer.Sent[^1]["free"]);
        Assert.NotNull(_peer.Sent[^1]["price"]);
    }

    [Fact]
    public void Pass_makes_only_its_page_free()
    {
        _sites.SetQuestPages([PassPage()]);

        Send(Offer(1, "https://www.ru.wikipedia.org/wiki/%D0%9E%D0%B3%D1%83%D1%80%D0%B5%D1%86/?oldid=1#История"));
        Send(Offer(2, "https://ru.wikipedia.org/wiki/Помидор"));
        Send(Offer(3, "https://ru.wikipedia.org/"));

        Assert.True((bool)_peer.Sent[0]["free"]!);
        Assert.Equal(1, (int)_peer.Sent[0]["id"]!);
        Assert.Null(_peer.Sent[1]["free"]);
        Assert.NotNull(_peer.Sent[1]["key"]);
        Assert.Null(_peer.Sent[2]["free"]);
    }

    [Fact]
    public void Watched_page_without_pass_is_still_sold()
    {
        _sites.SetQuestPages([new QuestPage("q", Cucumber, "https://ru.wikipedia.org/wiki/Огурец", Pass: false, WatchSelection: true, WatchScroll: true)]);

        Send(Offer(1, "https://ru.wikipedia.org/wiki/Огурец"));

        Assert.Null(_peer.Sent[0]["free"]);
    }

    [Fact]
    public void Page_visit_selection_and_scroll_are_reported_with_the_page_code_from_the_address()
    {
        _sites.SetQuestPages([PassPage()]);
        const string url = "https://www.ru.wikipedia.org/wiki/Огурец?x=1";

        Send(PageMessage("visit", url));
        Send(PageMessage("select", url, text: "огурец"));
        Send(PageMessage("scroll", url, share: 0.5));

        Assert.Equal(
            [PageActivityKind.Visit, PageActivityKind.Selection, PageActivityKind.Scroll],
            _reports.Select(report => report.Kind));
        Assert.All(_reports, report =>
        {
            Assert.Equal(1, report.PeerId);
            Assert.Equal(7, report.TabId);
            Assert.Equal(Cucumber, report.Page);
            Assert.Equal(url, report.Url);
            Assert.True(report.Focused);
        });
        Assert.Equal("огурец", _reports[1].Text);
        Assert.Null(_reports[1].Share);
        Assert.Equal(0.5, _reports[2].Share);
        Assert.Null(_reports[2].Text);
    }

    [Fact]
    public void Page_code_sent_by_the_browser_is_ignored()
    {
        _sites.SetQuestPages([PassPage()]);
        var message = PageMessage("visit", "https://ru.wikipedia.org/wiki/Помидор");
        message["page"] = Cucumber;

        Send(message);

        Assert.Empty(_reports);
    }

    [Fact]
    public void Selection_and_scroll_are_dropped_where_they_are_not_watched()
    {
        _sites.SetQuestPages([new QuestPage("listen", Track, "https://soundcloud.com/artist/track-name", Pass: false, WatchSelection: false, WatchScroll: false)]);
        const string url = "https://soundcloud.com/artist/track-name";

        Send(PageMessage("select", url, text: "слово"));
        Send(PageMessage("scroll", url, share: 1));
        Send(PageMessage("visit", url));

        Assert.Equal(PageActivityKind.Visit, Assert.Single(_reports).Kind);
    }

    [Fact]
    public void Long_selection_is_cut_and_share_is_clamped()
    {
        _sites.SetQuestPages([PassPage()]);
        const string url = "https://ru.wikipedia.org/wiki/Огурец";

        Send(PageMessage("select", url, text: new string('а', 500)));
        Send(PageMessage("select", url, text: new string('а', SiteLocks.MaxSelection - 1) + "😀"));
        Send(PageMessage("scroll", url, share: 3.5));
        Send(PageMessage("scroll", url, share: -1));

        Assert.Equal(SiteLocks.MaxSelection, _reports[0].Text!.Length);
        Assert.Equal(new string('а', SiteLocks.MaxSelection - 1), _reports[1].Text);
        Assert.Equal(1, _reports[2].Share);
        Assert.Equal(0, _reports[3].Share);
    }

    [Fact]
    public void Broken_page_messages_are_skipped_silently()
    {
        _sites.SetQuestPages([PassPage()]);
        const string url = "https://ru.wikipedia.org/wiki/Огурец";
        var bad = new List<JsonObject>
        {
            PageMessage("visit", "https://other.org/wiki/Огурец"),
            PageMessage("visit", "ftp://ru.wikipedia.org/wiki/Огурец"),
            PageMessage("jump", url),
            PageMessage("select", url),
            PageMessage("scroll", url),
            Without(PageMessage("visit", url), "tabId"),
            Without(PageMessage("visit", url), "url"),
            Without(PageMessage("visit", url), "focused"),
            Without(PageMessage("visit", url), "event"),
            With(PageMessage("visit", url), "tabId", "7"),
            With(PageMessage("visit", url), "tabId", 1.5),
            With(PageMessage("visit", url), "tabId", 1L << 40),
            With(PageMessage("visit", url), "focused", "yes"),
            With(PageMessage("scroll", url), "share", "0.5"),
            With(PageMessage("select", url), "text", 5),
            With(PageMessage("visit", url), "url", new JsonArray()),
        };

        foreach (var message in bad)
        {
            Send(message);
        }

        Assert.Empty(_reports);
        Assert.Empty(_peer.Sent);
    }

    [Fact]
    public void Page_messages_are_dropped_after_the_quest_is_gone()
    {
        _sites.SetQuestPages([PassPage()]);
        _sites.SetQuestPages([]);

        Send(PageMessage("visit", "https://ru.wikipedia.org/wiki/Огурец"));

        Assert.Empty(_reports);
    }

    private static QuestPage PassPage() =>
        new("read", Cucumber, "https://ru.wikipedia.org/wiki/Огурец", Pass: true, WatchSelection: true, WatchScroll: true);

    private static JsonObject Offer(int id, string url) => new() { ["type"] = "offer", ["id"] = id, ["url"] = url };

    private static JsonObject PageMessage(string kind, string url, string? text = null, double? share = null)
    {
        var message = new JsonObject
        {
            ["type"] = "page",
            ["tabId"] = 7,
            ["url"] = url,
            ["focused"] = true,
            ["event"] = kind,
        };
        if (text is not null)
        {
            message["text"] = text;
        }

        if (share is not null)
        {
            message["share"] = share;
        }

        return message;
    }

    private static JsonObject Without(JsonObject message, string name)
    {
        message.Remove(name);
        return message;
    }

    private static JsonObject With(JsonObject message, string name, JsonNode? value)
    {
        message[name] = value;
        return message;
    }

    private void Send(JsonObject message) => _sites.OnMessage(_peer, message);
}
