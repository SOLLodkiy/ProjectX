using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Modules;

/// <summary>
/// Контракт механики. Класс с открытым конструктором без параметров
/// в сборке modules/&lt;Имя&gt;/&lt;Имя&gt;.dll подключается автоматически.
/// </summary>
public interface IModule
{
    /// <summary>Постоянный уникальный код: по нему хранятся сохранения и настройки модуля.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Вызывается при запуске программы в потоке интерфейса и не должен его задерживать.</summary>
    void Start(IModuleContext context);

    /// <summary>Снимает все эффекты модуля. Подписки на шину ядро отменяет само.</summary>
    void Stop();
}

/// <summary>Всё, что ядро даёт модулю.</summary>
public interface IModuleContext
{
    IEventBus Events { get; }
    GameData Data { get; }
    TextCatalog Texts { get; }

    /// <summary>Кошелёк и формула дохода. Правки формулы ядро снимает при остановке модуля.</summary>
    IEconomy Economy { get; }

    /// <summary>Игровое состояние модуля, отдельное от других модулей.</summary>
    IKeyValueStore Saves { get; }

    /// <summary>Настройки модуля, отдельные от других модулей.</summary>
    IKeyValueStore Settings { get; }

    ILog Log { get; }
    bool DeveloperMode { get; }
}
