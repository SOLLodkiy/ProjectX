using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Modules;

internal sealed class ModuleContext(
    string moduleId,
    IEventBus events,
    GameData data,
    TextCatalog texts,
    IEconomy economy,
    IKeyValueStore saves,
    IKeyValueStore settings,
    ISystemChanges changes,
    ILog log,
    bool developerMode) : IModuleContext
{
    private readonly TrackingEventBus _events = new(events);
    private readonly TrackingEconomy _economy = new(economy);

    public IEventBus Events => _events;
    public GameData Data => data;
    public TextCatalog Texts => texts;
    public IEconomy Economy => _economy;
    public IKeyValueStore Saves { get; } = new ScopedStore(saves, $"modules/{moduleId}");
    public IKeyValueStore Settings { get; } = new ScopedStore(settings, $"modules/{moduleId}");
    public ISystemChanges Changes => changes;
    public ILog Log { get; } = new ScopedLog(log, moduleId);
    public bool DeveloperMode => developerMode;

    public void Release()
    {
        _events.ReleaseAll();
        _economy.ReleaseAll();
    }

    /// <summary>Запоминает правки формулы дохода, чтобы снять их при остановке модуля.</summary>
    private sealed class TrackingEconomy(IEconomy inner) : IEconomy
    {
        private readonly List<IDisposable> _modifiers = [];
        private readonly Lock _lock = new();

        public long Balance => inner.Balance;
        public long IncomePerMinute => inner.IncomePerMinute;

        public decimal Preview(string source, decimal baseAmount) => inner.Preview(source, baseAmount);

        public long Earn(string source, decimal baseAmount) => inner.Earn(source, baseAmount);

        public bool TrySpend(long amount, string reason) => inner.TrySpend(amount, reason);

        public IDisposable AddModifier(IncomeModifier modifier)
        {
            var added = inner.AddModifier(modifier);
            lock (_lock)
            {
                _modifiers.Add(added);
            }

            return added;
        }

        public void ReleaseAll()
        {
            lock (_lock)
            {
                _modifiers.ForEach(modifier => modifier.Dispose());
                _modifiers.Clear();
            }
        }
    }

    /// <summary>Запоминает подписки модуля, чтобы снять их при его остановке.</summary>
    private sealed class TrackingEventBus(IEventBus inner) : IEventBus
    {
        private readonly List<IDisposable> _subscriptions = [];
        private readonly Lock _lock = new();

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        {
            var subscription = inner.Subscribe(handler);
            lock (_lock)
            {
                _subscriptions.Add(subscription);
            }

            return subscription;
        }

        public void Publish<TEvent>(TEvent @event) => inner.Publish(@event);

        public void ReleaseAll()
        {
            lock (_lock)
            {
                _subscriptions.ForEach(subscription => subscription.Dispose());
                _subscriptions.Clear();
            }
        }
    }
}
