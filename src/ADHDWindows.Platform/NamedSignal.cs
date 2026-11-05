namespace ADHDWindows.Platform;

/// <summary>Сигнал между процессами программы. Обработчик вызывается в потоке из пула.</summary>
public sealed class NamedSignal : IDisposable
{
    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _wait;

    public NamedSignal(string name, Action onRaised)
    {
        _event = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, name);
        _wait = ThreadPool.RegisterWaitForSingleObject(
            _event,
            (_, _) => onRaised(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>Возвращает false, если сигнал никто не слушает.</summary>
    public static bool Raise(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _event.Dispose();
    }
}
