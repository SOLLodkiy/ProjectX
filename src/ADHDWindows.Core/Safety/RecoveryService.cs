using ADHDWindows.Core.Logging;

namespace ADHDWindows.Core.Safety;

/// <summary>Записывает изменения системы и откатывает те, что остались в реестре.</summary>
public sealed class RecoveryService(
    ChangeJournal journal,
    Func<IEnumerable<ISystemRestorer>> loadRestorers,
    ILog log,
    TimeProvider? clock = null)
{
    /// <summary>После стольких неудачных откатов запись удаляется, чтобы не повторять ошибку при каждом запуске.</summary>
    public const int MaxAttempts = 5;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Guid Session { get; } = Guid.NewGuid();

    public ISystemChanges For(string module) => new ModuleChanges(this, module);

    /// <summary>Откатывает всё. Вызывается только при запуске, пока этот запуск ничего не менял.</summary>
    public void RestoreAll() => Restore(_ => true);

    /// <summary>Откатывает изменения одного запуска и не трогает изменения более нового.</summary>
    public void RestoreSession(Guid session) => Restore(change => change.Session == session);

    private void Restore(Func<SystemChange, bool> filter)
    {
        try
        {
            var pending = journal.Read().Where(filter).Reverse().ToList();
            if (pending.Count == 0)
            {
                return;
            }

            log.Warn($"Откат изменений системы: {pending.Count}");

            // Сборки модулей загружаются только при необходимости: неисправный модуль не мешает обычной работе сторожа.
            var restorers = LoadRestorers();
            foreach (var change in pending)
            {
                RestoreOne(change, restorers);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            log.Error("Реестр изменений недоступен, откат будет повторён при следующем запуске", exception);
        }
    }

    private void RestoreOne(SystemChange change, Dictionary<string, ISystemRestorer> restorers)
    {
        try
        {
            if (!restorers.TryGetValue(change.Kind, out var restorer))
            {
                throw new InvalidOperationException($"Нет восстановителя для вида {change.Kind}");
            }

            restorer.Restore(change.Data);
        }
        catch (Exception exception)
        {
            var attempts = change.Attempts + 1;
            log.Error($"Не удалось откатить {change.Kind} ({change.Module}), попытка {attempts}", exception);
            if (attempts < MaxAttempts)
            {
                journal.Replace(change with { Attempts = attempts });
            }
            else
            {
                log.Error($"Откат {change.Kind} ({change.Module}) прекращён, данные: {change.Data}");
                journal.Remove(change.Id);
            }

            return;
        }

        journal.Remove(change.Id);
        log.Info($"Откачено: {change.Kind} ({change.Module})");
    }

    private Dictionary<string, ISystemRestorer> LoadRestorers()
    {
        var restorers = new Dictionary<string, ISystemRestorer>(StringComparer.Ordinal);
        foreach (var restorer in loadRestorers())
        {
            if (!restorers.TryAdd(restorer.Kind, restorer))
            {
                log.Error($"Восстановитель пропущен, вид {restorer.Kind} уже занят: {restorer.GetType().FullName}");
            }
        }

        return restorers;
    }

    private IDisposable Record(string module, string kind, string data)
    {
        var change = new SystemChange(Guid.NewGuid(), kind, data, module, Session, _clock.GetUtcNow());
        journal.Add(change);
        return new Mark(this, change);
    }

    private void Forget(SystemChange change)
    {
        try
        {
            journal.Remove(change.Id);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            // Запись останется, и повторный откат безопасен: так требует контракт восстановителя.
            log.Error($"Не удалось удалить запись {change.Kind} из реестра", exception);
        }
    }

    private sealed class ModuleChanges(RecoveryService service, string module) : ISystemChanges
    {
        public IDisposable Record(string kind, string data) => service.Record(module, kind, data);
    }

    private sealed class Mark(RecoveryService service, SystemChange change) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                service.Forget(change);
            }
        }
    }
}
