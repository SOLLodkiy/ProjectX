using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;
using ADHDWindows.Modules.Locks;
using ADHDWindows.Modules.Locks.Sites;
using ADHDWindows.Platform;
using Xunit;

namespace ADHDWindows.Tests;

public sealed class BrowserWatchTests
{
    private static readonly ProcessStamp Chrome = new(100, 1);
    private static readonly ProcessStamp Other = new(200, 1);

    private readonly RecordingLog _log = new();
    private readonly FakeTime _clock = new();
    private readonly FakeWindows _system = new();
    private readonly FakeBrowsers _browsers;
    private readonly EventBus _bus;
    private readonly List<object> _events = [];
    private readonly SiteCatalog _catalog;
    private readonly List<BrowserSession> _sessions = [];

    public BrowserWatchTests()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        _catalog = SiteCatalog.Load(data);
        _browsers = new FakeBrowsers(_system.Titles);
        _bus = new EventBus(_log);
        _bus.Subscribe<ExtensionProblemsChanged>(_events.Add);
        _bus.Subscribe<ExtensionScreenRequested>(_events.Add);
        _system.Processes[Chrome] = new ProgramFile(@"C:\Chrome\chrome.exe", "Google LLC", "Google Chrome", "chrome.exe", null, null, false, new Lazy<string>(() => "c"));
        _system.Processes[Other] = new ProgramFile(@"C:\Tool\tool.exe", "Acme", "Tool", "tool.exe", null, null, false, new Lazy<string>(() => "t"));
        _browsers.CommandLines[Chrome] = @"chrome.exe --user-data-dir=""D:\test profile\p"" --flag";
    }

    [Fact]
    public void Browser_without_extension_is_minimized_after_the_grace_time()
    {
        _system.Add(1, Chrome, "YouTube - Google Chrome");
        _system.Add(2, Other, "Tool");
        var watch = NewWatch();

        watch.Check(_sessions);
        Assert.Empty(_system.Calls);

        _clock.Advance(BrowserWatch.Grace);
        watch.Check(_sessions);

        Assert.Equal(["force 1"], _system.Calls);
        var problem = Assert.Single(watch.Problems);
        Assert.Equal(new ExtensionProblem("chrome", "Google Chrome", ExtensionProblemKind.Missing), problem);
        Assert.Single(_events.OfType<ExtensionScreenRequested>());
    }

    [Fact]
    public void Browser_with_connected_extension_is_left_alone()
    {
        _system.Add(1, Chrome, "YouTube - Google Chrome");
        Connect(incognito: false, "YouTube");
        var watch = NewWatch();

        for (var step = 0; step < 5; step++)
        {
            _clock.Advance(BrowserWatch.Grace);
            watch.Check(_sessions);
        }

        Assert.Empty(_system.Calls);
        Assert.Empty(watch.Problems);
    }

    [Fact]
    public void Window_unknown_to_the_extension_without_incognito_permission_is_minimized_once_the_difference_holds()
    {
        _system.Add(1, Chrome, "Новая вкладка - Google Chrome");
        _system.Add(2, Chrome, "Секрет - Google Chrome");
        Connect(incognito: false, "Новая вкладка");
        var watch = NewWatch();

        for (var step = 1; step < BrowserWatch.SettleChecks; step++)
        {
            watch.Check(_sessions);
            _clock.Advance(BrowserWatch.Settle);
        }

        Assert.Empty(_system.Calls);
        watch.Check(_sessions);

        Assert.Equal(["force 2"], _system.Calls);
        Assert.Equal(ExtensionProblemKind.Incognito, Assert.Single(watch.Problems).Kind);
    }

    [Fact]
    public void Title_that_catches_up_with_the_report_is_not_minimized()
    {
        _system.Add(1, Chrome, "Старая - Google Chrome");
        var session = Connect(incognito: false, "Новая");
        var watch = NewWatch();

        watch.Check(_sessions);
        _clock.Advance(BrowserWatch.Settle);
        watch.Check(_sessions);
        session.Titles = ["Старая"];
        _clock.Advance(BrowserWatch.Settle);
        watch.Check(_sessions);
        session.Titles = ["Новая"];
        _clock.Advance(BrowserWatch.Settle);
        watch.Check(_sessions);

        Assert.Empty(_system.Calls);
    }

    [Fact]
    public void Incognito_with_permission_is_not_minimized()
    {
        _system.Add(2, Chrome, "Секрет - Google Chrome");
        Connect(incognito: true, "Обычная");
        var watch = NewWatch();

        for (var step = 0; step < 5; step++)
        {
            _clock.Advance(BrowserWatch.Settle);
            watch.Check(_sessions);
        }

        Assert.Empty(_system.Calls);
    }

    [Fact]
    public void Extensions_page_stays_open_and_requested_page_opens_in_the_same_profile()
    {
        _system.Add(1, Chrome, "Расширения - Google Chrome");
        _system.Add(2, Chrome, "YouTube - Google Chrome");
        var watch = NewWatch();
        watch.Check(_sessions);
        _clock.Advance(BrowserWatch.Grace);
        watch.Check(_sessions);
        Assert.Equal(["force 2"], _system.Calls);

        watch.OpenExtensionsPage("chrome");
        _clock.Advance(BrowserWatch.Grace);
        watch.Check(_sessions);

        Assert.Equal(["force 2", "restore 2"], _system.Calls);
        var launch = Assert.Single(_browsers.Launched);
        Assert.Equal(@"C:\Chrome\chrome.exe", launch.Path);
        Assert.Equal([@"--user-data-dir=D:\test profile\p", "chrome://extensions"], launch.Arguments);

        _clock.Advance(BrowserWatch.SetupTime);
        watch.Check(_sessions);
        Assert.Equal("force 2", _system.Calls[^1]);
    }

    [Fact]
    public void Check_limited_to_a_profile_skips_other_browsers()
    {
        _system.Add(1, Chrome, "YouTube - Google Chrome");
        var watch = NewWatch(onlyProfile: @"D:\other");

        _clock.Advance(BrowserWatch.Grace);
        watch.Check(_sessions);

        Assert.Empty(_system.Calls);
    }

    [Fact]
    public void Release_returns_minimized_windows_and_clears_problems()
    {
        _system.Add(1, Chrome, "YouTube - Google Chrome");
        var watch = NewWatch();
        watch.Check(_sessions);
        _clock.Advance(BrowserWatch.Grace);
        watch.Check(_sessions);

        watch.ReleaseAll();

        Assert.Equal(["force 1", "restore 1"], _system.Calls);
        Assert.Empty(Assert.IsType<ExtensionProblemsChanged>(_events.OfType<ExtensionProblemsChanged>().Last()).Problems);
    }

    [Theory]
    [InlineData(@"chrome.exe --user-data-dir=""C:\a b"" x", new[] { @"--user-data-dir=C:\a b" })]
    [InlineData(@"chrome.exe --user-data-dir=C:\ab x", new[] { @"--user-data-dir=C:\ab" })]
    [InlineData(@"firefox.exe -profile ""C:\p q"" -foreground", new[] { "-profile", @"C:\p q" })]
    [InlineData(@"chrome.exe", new string[0])]
    public void Profile_is_read_from_the_command_line(string commandLine, string[] expected) =>
        Assert.Equal(expected, BrowserWatch.ProfileArguments(commandLine));

    [Fact]
    public void Bridge_delivers_messages_and_disconnects()
    {
        var name = $"ADHDWindows.Test.{Guid.NewGuid():N}";
        var received = new List<JsonObject?>();
        using var arrived = new SemaphoreSlim(0);
        using var bridge = new BrowserBridge(name, (peer, message) =>
        {
            lock (received)
            {
                received.Add(message);
            }

            if (message is not null)
            {
                peer.Send(new JsonObject { ["type"] = "echo", ["value"] = message["value"]?.DeepClone() });
            }

            arrived.Release();
        }, _log);
        bridge.Start();

        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            client.Connect(5000);
            MessageFrames.Write(client, JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["value"] = "привет" }));
            var reply = JsonNode.Parse(MessageFrames.Read(client)!)!;
            Assert.Equal("привет", (string?)reply["value"]);
        }

        Assert.True(arrived.Wait(5000));
        Assert.True(arrived.Wait(5000));
        Assert.Null(received[^1]);
    }

    [Fact]
    public void Frames_reject_wrong_length()
    {
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]);
        Assert.Throws<InvalidDataException>(() => MessageFrames.Read(stream));
        Assert.Null(MessageFrames.Read(new MemoryStream([1, 0])));
    }

    private BrowserWatch NewWatch(string? onlyProfile = null) =>
        new(_catalog, _system, _browsers, _bus, _log, _clock, onlyProfile);

    private BrowserSession Connect(bool incognito, params string[] titles)
    {
        var session = new BrowserSession(new SiteLockTests.FakePeer(1))
        {
            Browser = Chrome,
            Ready = true,
            IncognitoAllowed = incognito,
            Titles = titles,
        };
        _sessions.Add(session);
        return session;
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan time) => _now += time;
    }

    private sealed class FakeBrowsers(Dictionary<IntPtr, string> titles) : IBrowserSystem
    {

        public Dictionary<ProcessStamp, string> CommandLines { get; } = [];

        public List<(string Path, IReadOnlyList<string> Arguments)> Launched { get; } = [];

        public string Title(IntPtr window) => titles.GetValueOrDefault(window, string.Empty);

        public string? CommandLine(ProcessStamp process) => CommandLines.GetValueOrDefault(process);

        public string? ImagePath(ProcessStamp process) => process == Chrome ? @"C:\Chrome\chrome.exe" : null;

        public void Launch(string path, IReadOnlyList<string> arguments) => Launched.Add((path, arguments));
    }

    private sealed class FakeWindows : IProgramSystem
    {
        private readonly Dictionary<IntPtr, WindowInfo> _windows = [];

        public Dictionary<ProcessStamp, ProgramFile> Processes { get; } = [];

        public List<string> Calls { get; } = [];

        public void Add(IntPtr handle, ProcessStamp owner, string title)
        {
            _windows[handle] = new WindowInfo(handle, owner.Id, "Chrome_WidgetWin_1", false);
            Titles[handle] = title;
        }

        public Dictionary<IntPtr, string> Titles { get; } = [];

        public IReadOnlyList<WindowInfo> Windows() => [.. _windows.Values];

        public WindowInfo? Describe(IntPtr window) => _windows.GetValueOrDefault(window);

        IReadOnlyList<ProcessStamp> IProgramSystem.Processes() => [.. Processes.Keys];

        public ProcessStamp? Stamp(int processId) =>
            Processes.Keys.Where(stamp => stamp.Id == processId).Select(stamp => (ProcessStamp?)stamp).FirstOrDefault();

        public ProgramFile? ReadFile(ProcessStamp process) => Processes.GetValueOrDefault(process);

        public bool IsAlive(ProcessStamp process) => Processes.ContainsKey(process);

        public bool CanFreeze(ProcessStamp process) => true;

        public bool Freeze(ProcessStamp process) => true;

        public void Thaw(ProcessStamp process)
        {
        }

        public void MinimizeAndWait(IReadOnlyList<IntPtr> windows)
        {
        }

        public void ForceMinimize(IntPtr window)
        {
            Calls.Add($"force {window}");
            _windows[window] = _windows[window] with { IsMinimized = true };
        }

        public void Restore(IntPtr window)
        {
            Calls.Add($"restore {window}");
            _windows[window] = _windows[window] with { IsMinimized = false };
        }
    }
}
