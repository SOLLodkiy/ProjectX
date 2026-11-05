namespace ADHDWindows.Core.Safety;

/// <summary>Запись реестра: что изменено в системе и чем это откатить.</summary>
/// <param name="Kind">Код восстановителя, см. <see cref="ISystemRestorer.Kind"/>.</param>
/// <param name="Data">Всё, что нужно для отката: только строка, потому что откатывать может другой процесс.</param>
/// <param name="Session">Запуск программы, сделавший изменение.</param>
public sealed record SystemChange(
    Guid Id,
    string Kind,
    string Data,
    string Module,
    Guid Session,
    DateTimeOffset Created,
    int Attempts = 0);

/// <summary>Реестр изменений системы для модуля.</summary>
public interface ISystemChanges
{
    /// <summary>
    /// Записывает изменение на диск. Вызывается до применения изменения: если здесь произошла ошибка,
    /// применять нельзя. Модуль сам откатывает изменение, а затем вызывает Dispose у результата.
    /// Если он этого не сделает, откат выполнит программа или сторожевой процесс.
    /// </summary>
    IDisposable Record(string kind, string data);
}

/// <summary>
/// Откатывает изменения одного вида. Открытый класс с конструктором без параметров в сборке модуля
/// подключается автоматически, в том числе в сторожевом процессе, где самого модуля нет.
/// Restore может выполниться повторно или для изменения, которое не успели применить,
/// поэтому в этих случаях он ничего не должен менять и не должен завершаться ошибкой.
/// </summary>
public interface ISystemRestorer
{
    string Kind { get; }

    void Restore(string data);
}

/// <summary>Просьба сделать тестовое изменение системы. Только для режима разработчика.</summary>
public sealed record RecoveryProbeRequested(string Token);
