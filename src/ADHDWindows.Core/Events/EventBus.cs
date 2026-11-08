using ADHDWindows.Core.Logging;

namespace ADHDWindows.Core.Events;

/// <summary>Общая шина событий. Модули общаются через неё, не зная друг о друге.</summary>
public interface IEventBus
{
    /// <summary>Подписка на события ровно типа <typeparamref name="TEvent"/>. Dispose отменяет подписку.</summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler);

    /// <summary>Синхронно вызывает подписчиков в потоке отправителя.</summary>
    void Publish<TEvent>(TEvent @event);
}

public sealed record AppStarted;

public sealed record AppStopping;

/// <summary>Важное изменение, например покупка: сохранение записывается на диск сразу, не дожидаясь автосохранения.</summary>
public sealed record SaveRequested;

public sealed class EventBus(ILog log) : IEventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = [];
    private readonly Lock _lock = new();

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
            {
                _handlers[typeof(TEvent)] = handlers = [];
            }

            handlers.Add(handler);
        }

        return new Subscription(() => Unsubscribe(typeof(TEvent), handler));
    }

    public void Publish<TEvent>(TEvent @event)
    {
        Delegate[] snapshot;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
            {
                return;
            }

            snapshot = [.. handlers];
        }

        foreach (var handler in snapshot)
        {
            try
            {
                ((Action<TEvent>)handler)(@event);
            }
            catch (Exception ex)
            {
                log.Error($"Подписчик на {typeof(TEvent).Name} завершился с ошибкой", ex);
            }
        }
    }

    private void Unsubscribe(Type type, Delegate handler)
    {
        lock (_lock)
        {
            if (_handlers.TryGetValue(type, out var handlers))
            {
                handlers.Remove(handler);
            }
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
