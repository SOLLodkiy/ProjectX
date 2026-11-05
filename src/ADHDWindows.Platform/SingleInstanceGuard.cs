namespace ADHDWindows.Platform;

/// <summary>Не даёт запустить вторую копию программы в сеансе пользователя.</summary>
public sealed class SingleInstanceGuard(string name) : IDisposable
{
    public const string ApplicationName = @"Local\ADHDWindows.SingleInstance";

    private readonly Mutex _mutex = new(initiallyOwned: false, name);
    private bool _owned;

    public bool TryAcquire()
    {
        try
        {
            _owned = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // Прошлая копия завершилась аварийно, и блокировка перешла к этой.
            _owned = true;
        }

        return _owned;
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex.ReleaseMutex();
            _owned = false;
        }

        _mutex.Dispose();
    }
}
