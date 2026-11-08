using ADHDWindows.Core.Modules;

namespace ADHDWindows.Core.Hardships;

/// <summary>
/// Основа модуля усложнения. Наследнику достаточно описать себя и реализовать <see cref="Begin"/> и <see cref="End"/>:
/// связь с планировщиком через шину, повторные просьбы и снятие при остановке программы берёт на себя этот класс.
/// Изменения, переживающие сбой, наследник записывает в <see cref="IModuleContext.Changes"/> и откатывает
/// своим <see cref="Safety.ISystemRestorer"/>.
/// </summary>
public abstract class HardshipModule : IModule
{
    private readonly Lock _lock = new();
    private bool _active;

    public abstract HardshipInfo Info { get; }

    public string Id => "hardship." + Info.Id;

    public virtual string Name => Info.Id;

    /// <summary>Контекст модуля, доступен после запуска.</summary>
    protected IModuleContext Context { get; private set; } = null!;

    /// <summary>Работает ли усложнение сейчас.</summary>
    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    public void Start(IModuleContext context)
    {
        Context = context;
        context.Events.Subscribe<HardshipsRequested>(_ => context.Events.Publish(new HardshipOffered(Info)));
        context.Events.Subscribe<HardshipStartRequested>(request =>
        {
            if (request.Id == Info.Id)
            {
                Activate();
            }
        });
        context.Events.Subscribe<HardshipStopRequested>(request =>
        {
            if (request.Id == Info.Id)
            {
                Deactivate();
            }
        });
        OnStart();
        context.Events.Publish(new HardshipOffered(Info));
    }

    public void Stop()
    {
        Deactivate();
        OnStop();
    }

    /// <summary>Подготовка при запуске программы. Здесь усложнение ещё не действует.</summary>
    protected virtual void OnStart()
    {
    }

    /// <summary>Освобождение ресурсов при остановке программы, уже после <see cref="End"/>.</summary>
    protected virtual void OnStop()
    {
    }

    /// <summary>Включает эффект. Ошибка снимает усложнение и сообщает планировщику.</summary>
    protected abstract void Begin();

    /// <summary>Полностью снимает эффект. Вызывается один раз после успешного <see cref="Begin"/>.</summary>
    protected abstract void End();

    private void Activate()
    {
        lock (_lock)
        {
            if (_active)
            {
                return;
            }

            try
            {
                Begin();
                _active = true;
                Context.Log.Info($"Усложнение включено: {Info.Id}");
                return;
            }
            catch (Exception exception)
            {
                Context.Log.Error($"Усложнение не включилось: {Info.Id}", exception);
                SafeEnd();
            }
        }

        Context.Events.Publish(new HardshipFailed(Info.Id, "start"));
    }

    private void Deactivate()
    {
        lock (_lock)
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            SafeEnd();
            Context.Log.Info($"Усложнение снято: {Info.Id}");
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
            // Что не снялось, откатит реестр изменений при выходе или сторож после сбоя.
            Context.Log.Error($"Усложнение снято с ошибкой: {Info.Id}", exception);
        }
    }
}
