using ADHDWindows.Core.Data;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Modules;

internal sealed class ModuleContext(
    string moduleId,
    IEventBus events,
    GameData data,
    TextCatalog texts,
    IKeyValueStore saves,
    IKeyValueStore settings,
    ILog log,
    bool developerMode) : IModuleContext
{
    private readonly TrackingEventBus _events = new(events);

    public IEventBus Events => _events;
    public GameData Data => data;
    public TextCatalog Texts => texts;
    public IKeyValueStore Saves { get; } = new ScopedStore(saves, $"modules/{moduleId}");
    public IKeyValueStore Settings { get; } = new ScopedStore(settings, $"modules/{moduleId}");
    public ILog Log { get; } = new ScopedLog(log, moduleId);
    public bool DeveloperMode => developerMode;

    public void Release() => _events.ReleaseAll();

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
