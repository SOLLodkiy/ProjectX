using System.Text.Json.Nodes;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Media;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Locks;
using ADHDWindows.Modules.Locks.Sites;

namespace ADHDWindows.Tests;

public sealed class MediaReportTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly EventBus _bus;
    private readonly SiteLocks _sites;
    private readonly List<BrowserMediaReported> _reports = [];

    public MediaReportTests()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        _bus = new EventBus(_log);
        var store = new JsonFileStore(_temp.Combine("save.json"), _log);
        var economy = new ADHDWindows.Core.Economy.CoinEconomy(store, _bus, TimeProvider.System);
        _sites = new SiteLocks(new SiteClassifier(SiteCatalog.Load(data)), new UnlockLedger(store), economy, _bus, _log, TimeProvider.System);
        _bus.Subscribe<BrowserMediaReported>(_reports.Add);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Media_message_goes_to_the_bus_with_the_browser_process()
    {
        var peer = new SiteLockTests.FakePeer(3);
        _sites.OnMessage(peer, JsonNode.Parse("""{"type":"host","browserPid":1234,"browserStart":99}""")!.AsObject());
        _sites.OnMessage(peer, JsonNode.Parse("""
            {"type":"media","tabs":[{"tabId":7,"url":"https://www.youtube.com/watch?v=1","focused":true,
              "media":[{"id":"0:e1","video":true,"position":12.5,"duration":null,"playing":true,"rate":2,"audible":true,"title":"Ролик"}]}]}
            """)!.AsObject());

        var report = Assert.Single(_reports);
        Assert.Equal((3, 1234), (report.PeerId, report.BrowserProcessId!.Value));
        var tab = Assert.Single(report.Tabs);
        Assert.Equal((7, true), (tab.TabId, tab.Focused));
        Assert.Equal(new BrowserMediaElement("0:e1", true, 12.5, null, true, 2, true, "Ролик"), Assert.Single(tab.Media));
        Assert.Empty(peer.Sent);
    }

    [Fact]
    public void Disconnect_is_reported()
    {
        var peer = new SiteLockTests.FakePeer(5);
        _sites.OnMessage(peer, JsonNode.Parse("""{"type":"media","tabs":[]}""")!.AsObject());
        _sites.OnMessage(peer, null);
        Assert.True(_reports[^1].Disconnected);
        Assert.Equal(5, _reports[^1].PeerId);
    }

    [Fact]
    public void Broken_values_are_dropped_or_bounded()
    {
        var tabs = new JsonArray();
        for (var index = 0; index < MediaReport.MaxTabs + 10; index++)
        {
            tabs.Add(new JsonObject { ["tabId"] = index, ["url"] = "https://youtube.com/", ["media"] = new JsonArray() });
        }

        tabs.Add(new JsonObject { ["tabId"] = "seven", ["url"] = "https://youtube.com/" });
        var parsed = MediaReport.Parse(1, null, new JsonObject { ["tabs"] = tabs });
        Assert.Equal(MediaReport.MaxTabs, parsed.Tabs.Count);

        var element = MediaReport.Parse(1, null, JsonNode.Parse("""
            {"tabs":[{"tabId":1,"url":"https://youtube.com/","media":[
              {"id":"a","position":-5,"duration":"long","playing":"yes","rate":1000},
              {"position":3}]}]}
            """)!.AsObject()).Tabs[0].Media;
        var only = Assert.Single(element);
        Assert.Null(only.Position);
        Assert.Null(only.Duration);
        Assert.False(only.Playing);
        Assert.Equal(16, only.Rate);
    }
}
