using ADHDWindows.Core.Modules;

namespace ADHDWindows.Core.RandomEvents;

/// <summary>
/// Основа модуля случайного события. Наследник описывает себя и реализует <see cref="Begin"/> и <see cref="End"/>,
/// а когда игрок успел или не успел, вызывает <see cref="Complete"/>. Связь с планировщиком через шину
/// и снятие при остановке программы берёт на себя этот класс, поэтому новое событие подключается без правки ядра.
/// </summary>
public abstract class RandomEventModule : IModule
{
    // Включение и снятие идут по очереди под _lock, а флаги — под _state: поток оверлея вызывает Complete,
    // пока End под _lock ждёт его закрытия, и не должен на этом зависнуть.
    private readonly Lock _lock = new();
    private readonly Lock _state = new();
    private bool _active;
    private bool _completed;
    private int _generation;

    public abstract RandomEventInfo Info { get; }

    public string Id => "event." + Info.Id;

    public virtual string Name => Info.Id;

    protected IModuleContext Context { get; private set; } = null!;

    /// <summary>Когда планировщик закончит событие сам, если игрок не успеет.</summary>
    protected DateTimeOffset Ends { get; private set; }

    public bool IsActive
    {
        get
        {
            lock (_state)
            {
                return _active;
            }
        }
    }

    public void Start(IModuleContext context)
    {
        Context = context;
        context.Events.Subscribe<RandomEventsRequested>(_ => context.Events.Publish(new RandomEventOffered(Info)));
        context.Events.Subscribe<RandomEventStartRequested>(request =>
        {
            if (request.Id == Info.Id)
            {
                Activate(request.Variant, request.Ends);
            }
        });
        context.Events.Subscribe<RandomEventStopRequested>(request =>
        {
            if (request.Id == Info.Id)
            {
                Deactivate();
            }
        });
        OnStart();
        context.Events.Publish(new RandomEventOffered(Info));
    }

    public void Stop()
    {
        Deactivate();
        OnStop();
    }

    protected virtual void OnStart()
    {
    }

    protected virtual void OnStop()
    {
    }

    /// <summary>
    /// Начинает событие. False — сейчас его показать нечем (не сезон, нечего уценить): ничего не начато,
    /// планировщик выберет другое событие.
    /// </summary>
    protected abstract bool Begin(string? variant);

    /// <summary>Убирает всё, что показало событие. Вызывается один раз после успешного <see cref="Begin"/>.</summary>
    protected abstract void End();

    /// <summary>
    /// Игрок успел или не успел. Награду модуль зачисляет сам до вызова. Повторные вызовы и вызовы после снятия
    /// не действуют. Планировщик узнаёт об этом из пула потоков, поэтому вызывать можно из любого потока, в том числе
    /// из потока оверлея, который <see cref="End"/> закроет.
    /// </summary>
    protected void Complete(bool success, long reward)
    {
        int generation;
        lock (_state)
        {
            if (!_active || _completed)
            {
                return;
            }

            _completed = true;
            generation = _generation;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_state)
            {
                if (generation != _generation || !_active)
                {
                    return;
                }
            }

            Context.Events.Publish(new RandomEventFinished(Info.Id, success, reward));
        });
    }

    private void Activate(string? variant, DateTimeOffset ends)
    {
        string failure;
        lock (_lock)
        {
            lock (_state)
            {
                if (_active)
                {
                    return;
                }

                // Флаг ставится до Begin: игрок может успеть кликнуть, пока Begin ещё не вернулся.
                _active = true;
                _completed = false;
                _generation++;
            }

            Ends = ends;
            try
            {
                if (Begin(variant))
                {
                    Context.Log.Info($"Событие началось: {Info.Id}{(variant is null ? "" : $" ({variant})")}");
                    return;
                }

                MarkInactive();
                failure = "unavailable";
            }
            catch (Exception exception)
            {
                MarkInactive();
                Context.Log.Error($"Событие не началось: {Info.Id}", exception);
                SafeEnd();
                failure = "start";
            }
        }

        Context.Events.Publish(new RandomEventFailed(Info.Id, failure));
    }

    private void Deactivate()
    {
        lock (_lock)
        {
            if (MarkInactive())
            {
                SafeEnd();
            }
        }
    }

    /// <summary>Снимает флаг работы. False — событие и так не шло.</summary>
    private bool MarkInactive()
    {
        lock (_state)
        {
            if (!_active)
            {
                return false;
            }

            _active = false;
            _generation++;
            return true;
        }
    }

    private void SafeEnd()
    {
        try
        {
            End();
        }
        catch (Exception exception)
        {
            Context.Log.Error($"Событие убрано с ошибкой: {Info.Id}", exception);
        }
    }
}
