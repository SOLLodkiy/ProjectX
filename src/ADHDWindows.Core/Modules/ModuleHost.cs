using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Modules;

/// <summary>Запускает и останавливает модули. Ошибка одного модуля не затрагивает остальные.</summary>
public sealed class ModuleHost(
    IEventBus events,
    GameData data,
    TextCatalog texts,
    IEconomy economy,
    IKeyValueStore saves,
    IKeyValueStore settings,
    ILog log,
    bool developerMode)
{
    private readonly List<(IModule Module, ModuleContext Context)> _running = [];

    public IReadOnlyList<IModule> Running => _running.ConvertAll(entry => entry.Module);

    public void Start(IEnumerable<IModule> modules)
    {
        foreach (var module in modules)
        {
            if (_running.Exists(entry => entry.Module.Id == module.Id))
            {
                log.Error($"Модуль пропущен, код {module.Id} уже занят: {module.GetType().FullName}");
                continue;
            }

            var context = new ModuleContext(module.Id, events, data, texts, economy, saves, settings, log, developerMode);
            try
            {
                module.Start(context);
                _running.Add((module, context));
                log.Info($"Модуль запущен: {module.Id}");
            }
            catch (Exception ex)
            {
                context.Release();
                log.Error($"Модуль не запустился: {module.Id}", ex);
            }
        }
    }

    /// <summary>Останавливает модули в порядке, обратном запуску.</summary>
    public void StopAll()
    {
        for (var i = _running.Count - 1; i >= 0; i--)
        {
            var (module, context) = _running[i];
            try
            {
                module.Stop();
            }
            catch (Exception ex)
            {
                log.Error($"Модуль завершился с ошибкой: {module.Id}", ex);
            }

            context.Release();
        }

        _running.Clear();
    }
}
