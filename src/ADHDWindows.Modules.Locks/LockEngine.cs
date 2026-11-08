using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Safety;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Locks;

/// <summary>Решает, что заморозить и когда отпустить. Все методы вызываются из одного рабочего потока.</summary>
public sealed class LockEngine(
    IProgramSystem system,
    ProgramClassifier classifier,
    UnlockLedger ledger,
    IEconomy economy,
    ISystemChanges changes,
    IEventBus events,
    ILog log,
    TimeProvider clock)
{
    private const int VerdictCacheLimit = 4096;

    // Система показывает владельцем окна консоли запущенную в нём команду, а не сам терминал.
    private static readonly HashSet<string> TerminalClasses = ["ConsoleWindowClass", "PseudoConsoleWindow"];

    private readonly List<LockGroup> _groups = [];
    private readonly Dictionary<ProcessStamp, ProgramVerdict> _verdicts = [];
    private readonly HashSet<ProcessStamp> _noAccess = [];

    /// <summary>Осматривает все окна. Первый вызов блокирует уже открытые платные программы.</summary>
    public void Scan(CancellationToken token = default)
    {
        foreach (var window in system.Windows())
        {
            token.ThrowIfCancellationRequested();
            Consider(window, activated: false);
        }

        ForgetClosedPrograms();
        if (_verdicts.Count > VerdictCacheLimit)
        {
            _verdicts.Clear();
        }
    }

    /// <param name="window">Окно из события Windows.</param>
    /// <param name="activated">Пользователь переключился на окно, а не оно появилось само.</param>
    public void OnWindow(IntPtr window, bool activated)
    {
        if (system.Describe(window) is { } info)
        {
            Consider(info, activated);
        }
    }

    /// <summary>Забывает решения о процессах: например, появилась новая игра и уже открытый файл стал игрой.</summary>
    public void ForgetVerdicts() => _verdicts.Clear();

    public void Unlock(string key)
    {
        if (_groups.Find(group => group.Offer.Key == key) is not { } group)
        {
            return;
        }

        if (!economy.TrySpend(group.Offer.Price, $"unlock:{key}"))
        {
            events.Publish(new UnlockDenied(key, group.Offer.Price));
            return;
        }

        var when = clock.GetUtcNow();
        ledger.Add(group.Offer, when);
        _groups.Remove(group);
        Release(group);
        log.Info($"Разблокировано: {group.Offer.Name} за {group.Offer.Price}");
        events.Publish(new ItemUnlocked(group.Offer, when));
        PublishOffers();
    }

    /// <summary>Отпускает всё. Покупки не меняются: при следующем запуске программы заблокируются снова.</summary>
    public void ReleaseAll()
    {
        foreach (var group in _groups)
        {
            Release(group);
        }

        _groups.Clear();
    }

    private void Consider(WindowInfo window, bool activated)
    {
        if (TerminalClasses.Contains(window.ClassName)
            || system.Stamp(window.ProcessId) is not { } process
            || _noAccess.Contains(process)
            || Classify(process).Offer is not { } offer
            || ledger.Contains(offer.Key))
        {
            return;
        }

        // Запись ищется по процессу: после появления новой игры код того же процесса мог смениться.
        if (_groups.Find(item => item.Processes.Contains(process)) is { } owner)
        {
            OnFrozenWindow(owner, window, activated);
            return;
        }

        var group = _groups.Find(item => item.Offer.Key == offer.Key);

        var created = group is null;
        group ??= new LockGroup(offer);
        if (!Freeze(group))
        {
            return;
        }

        if (created)
        {
            _groups.Insert(0, group);
            log.Info($"Заблокировано: {offer.Name}, цена {offer.Price}");
            PublishOffers();
        }

        events.Publish(new LockScreenRequested(offer.Key));
    }

    private void OnFrozenWindow(LockGroup group, WindowInfo window, bool activated)
    {
        // Замороженное окно не свернётся само, если его развернули через панель задач.
        if (activated || !window.IsMinimized)
        {
            system.ForceMinimize(window.Handle);
        }

        if (activated)
        {
            events.Publish(new LockScreenRequested(group.Offer.Key));
        }
    }

    /// <summary>Замораживает все ещё работающие процессы программы, например вспомогательные процессы Electron.</summary>
    private bool Freeze(LockGroup group)
    {
        var fresh = system.Processes()
            // Процесс из другой записи уже заморожен: повторная заморозка потребовала бы двух разморозок.
            .Where(process => !_groups.Any(other => other.Processes.Contains(process)) && Classify(process).Offer?.Key == group.Offer.Key)
            .Where(HasAccess)
            .ToList();
        if (fresh.Count == 0)
        {
            return false;
        }

        var ids = fresh.Select(process => process.Id).ToHashSet();
        var windows = system.Windows()
            .Where(window => ids.Contains(window.ProcessId) && !window.IsMinimized)
            .Select(window => window.Handle)
            .ToList();

        // Сворачивается до заморозки: потом программа уже не сможет ответить на просьбу.
        system.MinimizeAndWait(windows);

        IDisposable mark;
        try
        {
            mark = changes.Record(
                FreezeRestorer.ChangeKind,
                new FreezeRecord(fresh, [.. windows.Select(window => window.ToInt64())]).ToData());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Без записи откат после сбоя невозможен, поэтому морозить нельзя.
            log.Error($"Не удалось записать заморозку: {group.Offer.Name}", exception);
            RestoreWindows(windows);
            return false;
        }

        var frozen = fresh.Where(TryFreeze).ToList();
        if (frozen.Count == 0)
        {
            mark.Dispose();
            RestoreWindows(windows);
            return false;
        }

        group.Add(frozen, windows, mark);
        return true;
    }

    private bool TryFreeze(ProcessStamp process)
    {
        if (system.Freeze(process))
        {
            return true;
        }

        _noAccess.Add(process);
        return false;
    }

    private bool HasAccess(ProcessStamp process)
    {
        if (system.CanFreeze(process))
        {
            return true;
        }

        if (_noAccess.Add(process))
        {
            log.Warn($"Нет прав на заморозку процесса {process.Id}: он не блокируется");
        }

        return false;
    }

    private ProgramVerdict Classify(ProcessStamp process)
    {
        if (!_verdicts.TryGetValue(process, out var verdict))
        {
            verdict = classifier.Classify(system.ReadFile(process));
            _verdicts[process] = verdict;
        }

        return verdict;
    }

    /// <summary>Пользователь может завершить замороженную программу в Диспетчере задач.</summary>
    private void ForgetClosedPrograms()
    {
        var changed = false;
        foreach (var group in _groups.ToList())
        {
            group.Processes.RemoveWhere(process => !system.IsAlive(process));
            if (group.Processes.Count == 0)
            {
                _groups.Remove(group);
                group.Forget();
                changed = true;
            }
        }

        if (changed)
        {
            PublishOffers();
        }
    }

    private void Release(LockGroup group)
    {
        foreach (var process in group.Processes)
        {
            system.Thaw(process);
        }

        RestoreWindows(group.Windows);
        group.Forget();
    }

    private void RestoreWindows(IEnumerable<IntPtr> windows)
    {
        foreach (var window in windows)
        {
            system.Restore(window);
        }
    }

    private void PublishOffers() =>
        events.Publish(new LockOffersChanged([.. _groups.Select(group => group.Offer)]));

    private sealed class LockGroup(LockOffer offer)
    {
        private readonly List<IDisposable> _marks = [];

        public LockOffer Offer { get; } = offer;

        public HashSet<ProcessStamp> Processes { get; } = [];

        public List<IntPtr> Windows { get; } = [];

        public void Add(IEnumerable<ProcessStamp> processes, IEnumerable<IntPtr> windows, IDisposable mark)
        {
            Processes.UnionWith(processes);
            Windows.AddRange(windows);
            _marks.Add(mark);
        }

        public void Forget()
        {
            _marks.ForEach(mark => mark.Dispose());
            _marks.Clear();
        }
    }
}
