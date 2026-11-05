using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Locks;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

public sealed class LockEngineTests : IDisposable
{
    private const long Price = 300;
    private const string Key = "file:acme|tool|tool.exe";

    private static readonly ProcessStamp Tool = new(10, 1);
    private static readonly ProcessStamp Helper = new(11, 1);
    private static readonly ProcessStamp Free = new(20, 1);

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly EventBus _bus;
    private readonly JsonFileStore _store;
    private readonly CoinEconomy _economy;
    private readonly ChangeJournal _journal;
    private readonly FakeSystem _system = new();
    private readonly List<object> _events = [];
    private LockEngine _engine;

    public LockEngineTests()
    {
        _bus = new EventBus(_log);
        _store = new JsonFileStore(_temp.Combine("store.json"), _log);
        _economy = new CoinEconomy(_store, _bus, TimeProvider.System);
        _journal = new ChangeJournal(_temp.Combine("recovery.json"), _log);
        _bus.Subscribe<LockOffersChanged>(_events.Add);
        _bus.Subscribe<LockScreenRequested>(_events.Add);
        _bus.Subscribe<UnlockDenied>(_events.Add);
        _bus.Subscribe<ItemUnlocked>(_events.Add);

        _system.AddProcess(Tool, ToolFile());
        _system.AddProcess(Helper, ToolFile());
        _system.AddProcess(Free, new ProgramFile(@"C:\free\claude.exe", "Anthropic", "Claude", null, null, null, false, new Lazy<string>(() => "free")));
        _engine = NewEngine();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Paid_window_is_minimized_recorded_and_only_then_frozen()
    {
        _system.AddWindow(1, Tool);

        _engine.Scan();

        Assert.Equal(["minimize 1", "journal", "freeze 10", "freeze 11"], _system.Calls);
        var offer = Assert.Single(Offers());
        Assert.Equal(new LockOffer(Key, "Tool", Price, LockKind.Program), offer);
        Assert.Equal(Key, Assert.Single(_events.OfType<LockScreenRequested>()).Key);
    }

    [Fact]
    public void Journal_record_holds_every_process_and_window_once()
    {
        _system.AddWindow(1, Tool);

        _engine.Scan();

        var change = Assert.Single(_journal.Read());
        Assert.Equal(FreezeRestorer.ChangeKind, change.Kind);
        var record = FreezeRecord.Parse(change.Data)!;
        Assert.Equal([Tool, Helper], record.Processes);
        Assert.Equal([1L], record.Windows);
    }

    [Fact]
    public void Free_program_is_left_alone()
    {
        _system.AddWindow(2, Free);

        _engine.Scan();

        Assert.Empty(_system.Calls);
        Assert.Empty(_events);
    }

    [Fact]
    public void Console_window_is_left_alone_whoever_seems_to_own_it()
    {
        _system.AddWindow(1, Tool, className: "ConsoleWindowClass");

        _engine.Scan();

        Assert.Empty(_system.Calls);
    }

    [Fact]
    public void Repeated_scans_do_not_freeze_twice()
    {
        _system.AddWindow(1, Tool);

        _engine.Scan();
        _engine.Scan();

        Assert.Equal(1, _system.Calls.Count(call => call == "freeze 10"));
        Assert.Single(_events.OfType<LockScreenRequested>());
    }

    [Fact]
    public void Purchase_spends_coins_thaws_and_lasts_forever()
    {
        _economy.Earn("test", 1000);
        _system.AddWindow(1, Tool);
        _engine.Scan();
        _system.Calls.Clear();

        _engine.Unlock(Key);

        Assert.Equal(700, _economy.Balance);
        Assert.Equal(["thaw 10", "thaw 11", "restore 1"], _system.Calls);
        Assert.Empty(_journal.Read());
        Assert.Equal(Key, Assert.Single(_events.OfType<ItemUnlocked>()).Offer.Key);
        Assert.Empty(Offers());

        _system.Calls.Clear();
        _engine = NewEngine();
        _engine.Scan();
        Assert.Empty(_system.Calls);
    }

    [Fact]
    public void Second_purchase_request_is_ignored()
    {
        _economy.Earn("test", 1000);
        _system.AddWindow(1, Tool);
        _engine.Scan();

        _engine.Unlock(Key);
        _engine.Unlock(Key);

        Assert.Equal(700, _economy.Balance);
    }

    [Fact]
    public void Not_enough_coins_keeps_the_program_frozen()
    {
        _economy.Earn("test", 299);
        _system.AddWindow(1, Tool);
        _engine.Scan();
        _system.Calls.Clear();

        _engine.Unlock(Key);

        Assert.Equal(299, _economy.Balance);
        Assert.Empty(_system.Calls);
        Assert.Single(_journal.Read());
        Assert.Equal(Key, Assert.Single(_events.OfType<UnlockDenied>()).Key);
    }

    [Fact]
    public void Window_that_was_already_minimized_stays_minimized_after_purchase()
    {
        _economy.Earn("test", 1000);
        _system.AddWindow(1, Tool, minimized: true);
        _engine.Scan();
        _system.Calls.Clear();

        _engine.Unlock(Key);

        Assert.DoesNotContain("restore 1", _system.Calls);
    }

    [Fact]
    public void New_copy_of_a_frozen_program_joins_its_group()
    {
        _system.AddWindow(1, Tool);
        _engine.Scan();
        var copy = new ProcessStamp(12, 1);
        _system.AddProcess(copy, ToolFile());
        _system.AddWindow(3, copy);

        _engine.OnWindow(3, activated: false);

        Assert.Contains("freeze 12", _system.Calls);
        Assert.Equal(2, _journal.Read().Count);
        Assert.Single(Offers());
    }

    [Fact]
    public void Switching_to_a_frozen_window_reopens_the_screen()
    {
        _system.AddWindow(1, Tool, minimized: true);
        _engine.Scan();
        _events.Clear();
        _system.Calls.Clear();

        _engine.OnWindow(1, activated: true);

        Assert.Equal(["force 1"], _system.Calls);
        Assert.Equal(Key, Assert.Single(_events.OfType<LockScreenRequested>()).Key);
    }

    [Fact]
    public void Frozen_window_opened_from_taskbar_is_minimized_by_the_scan()
    {
        _system.AddWindow(1, Tool, minimized: true);
        _engine.Scan();
        _system.AddWindow(1, Tool);
        _system.Calls.Clear();
        _events.Clear();

        _engine.Scan();

        Assert.Equal(["force 1"], _system.Calls);
        Assert.Empty(_events);
    }

    [Fact]
    public void Program_without_rights_is_not_locked()
    {
        _system.Forbidden.UnionWith([Tool, Helper]);
        _system.AddWindow(1, Tool);

        _engine.Scan();
        _engine.Scan();

        Assert.Empty(_system.Calls);
        Assert.Empty(_events);
        Assert.Empty(_journal.Read());
    }

    [Fact]
    public void Nothing_is_frozen_when_journal_cannot_be_written()
    {
        var engine = NewEngine(new FailingChanges());
        _system.AddWindow(1, Tool);

        engine.Scan();

        Assert.Equal(["minimize 1", "restore 1"], _system.Calls);
        Assert.Empty(_events);
    }

    [Fact]
    public void Stop_thaws_everything_and_clears_journal_but_buys_nothing()
    {
        _system.AddWindow(1, Tool);
        _engine.Scan();
        _system.Calls.Clear();

        _engine.ReleaseAll();

        Assert.Equal(["thaw 10", "thaw 11", "restore 1"], _system.Calls);
        Assert.Empty(_journal.Read());

        _system.Calls.Clear();
        _engine = NewEngine();
        _engine.Scan();
        Assert.Contains("freeze 10", _system.Calls);
    }

    [Fact]
    public void Killed_frozen_program_leaves_the_list()
    {
        _system.AddWindow(1, Tool);
        _engine.Scan();
        _system.Kill(Tool);
        _system.Kill(Helper);

        _engine.Scan();

        Assert.Empty(Offers());
        Assert.Empty(_journal.Read());
    }

    private static ProgramFile ToolFile() =>
        new(@"C:\tools\tool.exe", "Acme", "Tool", "tool.exe", "Tool", null, false, new Lazy<string>(() => "tool"));

    private IReadOnlyList<LockOffer> Offers() => _events.OfType<LockOffersChanged>().Last().Offers;

    private LockEngine NewEngine(ISystemChanges? changes = null)
    {
        var catalog = new ProgramCatalog(
            Price,
            [],
            [],
            [new FreeEntry("claude", [new FileMatch(Company: "Anthropic*")])]);
        var recovery = new RecoveryService(_journal, () => [], _log);
        return new LockEngine(
            _system,
            new ProgramClassifier(catalog, []),
            new UnlockLedger(_store),
            _economy,
            changes ?? new NotingChanges(recovery.For("locks"), _system.Calls),
            _bus,
            _log,
            TimeProvider.System);
    }

    private sealed class NotingChanges(ISystemChanges inner, List<string> calls) : ISystemChanges
    {
        public IDisposable Record(string kind, string data)
        {
            calls.Add("journal");
            return inner.Record(kind, data);
        }
    }

    private sealed class FailingChanges : ISystemChanges
    {
        public IDisposable Record(string kind, string data) => throw new IOException("диск заполнен");
    }

    private sealed class FakeSystem : IProgramSystem
    {
        private readonly Dictionary<ProcessStamp, ProgramFile> _processes = [];
        private readonly Dictionary<IntPtr, WindowInfo> _windows = [];

        public List<string> Calls { get; } = [];

        public HashSet<ProcessStamp> Forbidden { get; } = [];

        public void AddProcess(ProcessStamp stamp, ProgramFile file) => _processes[stamp] = file;

        public void AddWindow(IntPtr handle, ProcessStamp owner, bool minimized = false, string className = "App") =>
            _windows[handle] = new WindowInfo(handle, owner.Id, className, minimized);

        public void Kill(ProcessStamp stamp) => _processes.Remove(stamp);

        public IReadOnlyList<WindowInfo> Windows() => [.. _windows.Values];

        public WindowInfo? Describe(IntPtr window) => _windows.GetValueOrDefault(window);

        public IReadOnlyList<ProcessStamp> Processes() => [.. _processes.Keys];

        public ProcessStamp? Stamp(int processId) =>
            _processes.Keys.Where(stamp => stamp.Id == processId).Select(stamp => (ProcessStamp?)stamp).FirstOrDefault();

        public ProgramFile? ReadFile(ProcessStamp process) => _processes.GetValueOrDefault(process);

        public bool IsAlive(ProcessStamp process) => _processes.ContainsKey(process);

        public bool CanFreeze(ProcessStamp process) => !Forbidden.Contains(process);

        public bool Freeze(ProcessStamp process)
        {
            Calls.Add($"freeze {process.Id}");
            return true;
        }

        public void Thaw(ProcessStamp process) => Calls.Add($"thaw {process.Id}");

        public void MinimizeAndWait(IReadOnlyList<IntPtr> windows)
        {
            foreach (var window in windows)
            {
                Calls.Add($"minimize {window}");
            }
        }

        public void ForceMinimize(IntPtr window) => Calls.Add($"force {window}");

        public void Restore(IntPtr window) => Calls.Add($"restore {window}");
    }
}
