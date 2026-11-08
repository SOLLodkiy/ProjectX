using System.Text.Json.Nodes;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Locks;
using ADHDWindows.Modules.Locks.Sites;
using Xunit;

namespace ADHDWindows.Tests;

public sealed class SiteLockTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly SiteCatalog _catalog;
    private readonly SiteClassifier _classifier;
    private readonly EventBus _bus;
    private readonly JsonFileStore _store;
    private readonly CoinEconomy _economy;
    private readonly UnlockLedger _ledger;
    private readonly SiteLocks _sites;
    private readonly FakePeer _peer = new(1);
    private readonly List<object> _events = [];

    public SiteLockTests()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        _catalog = SiteCatalog.Load(data);
        _classifier = new SiteClassifier(_catalog);
        _bus = new EventBus(_log);
        _store = new JsonFileStore(_temp.Combine("save.json"), _log);
        _economy = new CoinEconomy(_store, _bus, TimeProvider.System);
        _ledger = new UnlockLedger(_store);
        _sites = new SiteLocks(_classifier, _ledger, _economy, _bus, _log, TimeProvider.System);
        _bus.Subscribe<ItemUnlocked>(_events.Add);
        _bus.Subscribe<UnlockDenied>(_events.Add);
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("https://WWW.YouTube.com./watch?v=1", "youtube.com")]
    [InlineData("http://music.yandex.ru:8080/", "music.yandex.ru")]
    [InlineData("https://www.com/", "www.com")]
    [InlineData("https://кто.рф/", "xn--j1ail.xn--p1ai")]
    [InlineData("http://127.0.0.1:5000/", "127.0.0.1")]
    [InlineData("chrome://extensions", null)]
    [InlineData("file:///C:/a.html", null)]
    [InlineData("not a url", null)]
    public void Site_name_is_taken_from_the_address(string url, string? expected) =>
        Assert.Equal(expected, Domain.FromUrl(url));

    [Theory]
    [InlineData("news.example.com", "example.com")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("example.com", "example.com")]
    [InlineData("user.github.io", "user.github.io")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void Unknown_site_is_bought_as_a_whole(string host, string expected) =>
        Assert.Equal(expected, Domain.Registrable(host, _catalog.MultiPartSuffixes));

    [Theory]
    [InlineData("claude.ai")]
    [InlineData("claude.com")]
    [InlineData("www.anthropic.com")]
    [InlineData("docs.anthropic.com")]
    [InlineData("accounts.google.com")]
    [InlineData("www.gosuslugi.ru")]
    [InlineData("localhost")]
    public void Always_free_sites_have_no_price(string host) =>
        Assert.Null(_classifier.Classify(host).Offer);

    [Theory]
    [InlineData("youtube.com", "site:youtube.com", 800)]
    [InlineData("m.youtube.com", "site:youtube.com", 800)]
    [InlineData("music.yandex.ru", "site:music.yandex.ru", 800)]
    [InlineData("maps.yandex.ru", "site:yandex.ru", 25)]
    [InlineData("mail.google.com", "site:mail.google.com", 150)]
    [InlineData("news.example.co.uk", "site:example.co.uk", 100)]
    public void Site_price_comes_from_the_most_exact_entry(string host, string key, long price)
    {
        var offer = _classifier.Classify(host).Offer!;

        Assert.Equal(key, offer.Key);
        Assert.Equal(price, offer.Price);
        Assert.Equal(LockKind.Site, offer.Kind);
    }

    [Fact]
    public void Site_catalog_follows_the_idea()
    {
        Assert.Equal(100, _catalog.DefaultPrice);
        Assert.Contains("claude.ai", _catalog.Free);
        Assert.Equal(["chrome", "edge", "yandex", "firefox"], _catalog.Browsers.Select(browser => browser.Id));
        Assert.True(_catalog.Sites.Count >= 60);
    }

    [Fact]
    public void Broken_site_catalog_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => (_catalog with { Sites = [.. _catalog.Sites, new SiteEntry("free.example", "Free", 0)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (_catalog with { Free = [.. _catalog.Free, "Claude.AI"] }).Validate());
        Assert.Throws<InvalidDataException>(() => (_catalog with { Sites = [.. _catalog.Sites, _catalog.Sites[0]] }).Validate());
    }

    [Theory]
    [InlineData("site:com")]
    [InlineData("site:co.uk")]
    [InlineData("site:github.io")]
    [InlineData("site:claude.ai")]
    [InlineData("site:m.youtube.com")]
    [InlineData("site:YouTube.com")]
    [InlineData("program:steam")]
    [InlineData("site:")]
    public void Forged_purchase_codes_are_refused(string key) => Assert.Null(_classifier.OfferFor(key));

    [Fact]
    public void Rules_open_free_and_bought_sites_and_close_priced_subdomains()
    {
        var rules = _classifier.Rules(["site:yandex.ru", "site:example.com", "program:steam"]);

        Assert.True(SiteClassifier.IsAllowed(rules, "claude.ai"));
        Assert.True(SiteClassifier.IsAllowed(rules, "maps.yandex.ru"));
        Assert.False(SiteClassifier.IsAllowed(rules, "music.yandex.ru"));
        Assert.True(SiteClassifier.IsAllowed(rules, "a.example.com"));
        Assert.False(SiteClassifier.IsAllowed(rules, "youtube.com"));
        Assert.False(SiteClassifier.IsAllowed(rules, "unknown.org"));
    }

    [Fact]
    public void Rules_agree_with_the_price_list_for_every_site()
    {
        string[] bought = ["site:yandex.ru", "site:music.yandex.ru", "site:google.com", "site:example.com", "site:github.io"];
        var rules = _classifier.Rules(bought);
        var hosts = _catalog.Sites.Select(site => site.Domain)
            .Concat(_catalog.Free)
            .Concat(_catalog.Sites.Select(site => "sub." + site.Domain))
            .Concat(_catalog.Free.Select(domain => "sub." + domain))
            .Concat(["example.com", "a.example.com", "other.org", "user.github.io", "a.user.github.io"]);

        foreach (var host in hosts)
        {
            var offer = _classifier.Classify(host).Offer;
            var expected = offer is null || bought.Contains(offer.Key);
            Assert.True(expected == SiteClassifier.IsAllowed(rules, host), host);
        }
    }

    [Fact]
    public void Hello_gets_the_rules()
    {
        Send(new JsonObject { ["type"] = "host", ["browserPid"] = 42, ["browserStart"] = 7 });
        Send(new JsonObject { ["type"] = "hello", ["browser"] = "chrome", ["incognito"] = true });

        var state = Assert.Single(_peer.Sent);
        Assert.Equal("state", (string?)state["type"]);
        Assert.Contains(state["rules"]!.AsArray(), rule => (string?)rule!["domain"] == "claude.ai" && (bool)rule["allow"]!);
        var session = Assert.Single(_sites.Sessions);
        Assert.True(session.Ready);
        Assert.True(session.IncognitoAllowed);
        Assert.Equal(42, session.Browser?.Id);
    }

    [Fact]
    public void Offer_reports_price_and_balance_or_that_the_site_is_free()
    {
        _economy.Earn("test", 500);

        Send(new JsonObject { ["type"] = "offer", ["id"] = 5, ["url"] = "https://www.youtube.com/watch" });
        Send(new JsonObject { ["type"] = "offer", ["id"] = 6, ["url"] = "https://claude.ai/new" });

        Assert.Equal(5, (int)_peer.Sent[0]["id"]!);
        Assert.Equal("site:youtube.com", (string?)_peer.Sent[0]["key"]);
        Assert.Equal(800, (long)_peer.Sent[0]["price"]!);
        Assert.Equal(500, (long)_peer.Sent[0]["balance"]!);
        Assert.True((bool)_peer.Sent[1]["free"]!);
    }

    [Fact]
    public void Purchase_spends_coins_once_and_sends_new_rules_before_the_answer()
    {
        _economy.Earn("test", 1000);
        Send(new JsonObject { ["type"] = "hello" });
        _peer.Sent.Clear();

        Send(new JsonObject { ["type"] = "unlock", ["id"] = 1, ["key"] = "site:youtube.com" });
        Send(new JsonObject { ["type"] = "unlock", ["id"] = 2, ["key"] = "site:youtube.com" });

        Assert.Equal(200, _economy.Balance);
        Assert.Equal(["state", "reply", "reply"], _peer.Sent.Select(message => (string?)message["type"]));
        Assert.True((bool)_peer.Sent[1]["unlocked"]!);
        Assert.True((bool)_peer.Sent[2]["unlocked"]!);
        var unlocked = Assert.Single(_events.OfType<ItemUnlocked>());
        Assert.Equal(new LockOffer("site:youtube.com", "YouTube", 800, LockKind.Site), unlocked.Offer);
        Assert.True(new UnlockLedger(_store).Contains("site:youtube.com"));
    }

    [Fact]
    public void Purchase_without_coins_or_with_a_forged_code_is_refused()
    {
        _economy.Earn("test", 50);

        Send(new JsonObject { ["type"] = "unlock", ["id"] = 1, ["key"] = "site:youtube.com" });
        Send(new JsonObject { ["type"] = "unlock", ["id"] = 2, ["key"] = "site:com" });

        Assert.True((bool)_peer.Sent[0]["denied"]!);
        Assert.Equal("unknown", (string?)_peer.Sent[1]["error"]);
        Assert.Equal(50, _economy.Balance);
        Assert.Single(_events.OfType<UnlockDenied>());
        Assert.Empty(_events.OfType<ItemUnlocked>());
    }

    [Fact]
    public void Disconnected_browser_is_forgotten()
    {
        Send(new JsonObject { ["type"] = "hello" });
        _sites.OnMessage(_peer, null);

        Assert.Empty(_sites.Sessions);
    }

    private void Send(JsonObject message) => _sites.OnMessage(_peer, message);

    internal sealed class FakePeer(int id) : IBrowserPeer
    {
        public int Id => id;

        public List<JsonObject> Sent { get; } = [];

        public void Send(JsonObject message) => Sent.Add(message);
    }
}
