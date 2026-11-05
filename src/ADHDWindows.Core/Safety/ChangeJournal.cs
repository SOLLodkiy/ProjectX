using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Safety;

/// <summary>
/// Файл реестра изменений. Каждая операция заново читает файл и сразу записывает его на диск:
/// после сбоя данные должны быть актуальны, а работать с файлом могут программа и сторожевой процесс.
/// </summary>
public sealed class ChangeJournal(string path, ILog log)
{
    private const string ChangesKey = "changes";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly string _mutexName = ScopedName.ForPath("ADHDWindows.Recovery", path);

    public IReadOnlyList<SystemChange> Read() => Locked(store => store.Get<List<SystemChange>>(ChangesKey) ?? []);

    public void Add(SystemChange change) => Change(changes => changes.Add(change));

    public void Remove(Guid id) => Change(changes => changes.RemoveAll(change => change.Id == id));

    public void Replace(SystemChange change) => Change(changes =>
    {
        var index = changes.FindIndex(existing => existing.Id == change.Id);
        if (index >= 0)
        {
            changes[index] = change;
        }
    });

    private void Change(Action<List<SystemChange>> edit) => Locked(store =>
    {
        var changes = store.Get<List<SystemChange>>(ChangesKey) ?? [];
        edit(changes);
        store.Set(ChangesKey, changes);
        store.Flush();
        return changes;
    });

    private T Locked<T>(Func<JsonFileStore, T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(LockTimeout);
        }
        catch (AbandonedMutexException)
        {
            // Владелец завершился аварийно. Запись файла атомарна, поэтому файл цел.
            owned = true;
        }

        if (!owned)
        {
            throw new TimeoutException($"Реестр изменений занят: {path}");
        }

        try
        {
            var store = new JsonFileStore(path, log);
            store.Load();
            return action(store);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
