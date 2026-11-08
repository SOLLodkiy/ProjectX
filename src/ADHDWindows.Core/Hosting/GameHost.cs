using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Stats;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Hosting;

/// <summary>Собирает ядро и управляет его жизненным циклом. Не зависит от интерфейса.</summary>
public sealed class GameHost : IDisposable
{
    public const string LanguageSettingKey = "language";
    public const string DefaultLanguage = "ru";

    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(1);

    private readonly AppPaths _paths;
    private readonly ILog _log;
    private readonly JsonFileStore _saves;
    private readonly JsonFileStore _settings;
    private readonly EventBus _events;
    private readonly CoinEconomy _economy;
    private readonly RecoveryService _recovery;
    private readonly ModuleHost _modules;
    private Timer? _autosave;
    private bool _started;

    public GameHost(AppPaths paths, AppOptions options, ILog log, TimeProvider? clock = null)
    {
        _paths = paths;
        _log = log;
        _saves = new JsonFileStore(paths.SavePath, log);
        _settings = new JsonFileStore(paths.SettingsPath, log);
        _events = new EventBus(log);
        _economy = new CoinEconomy(_saves, _events, clock ?? TimeProvider.System);
        Data = new GameData(paths.DataDir, log);
        Texts = new TextCatalog(Data, DefaultLanguage, log);
        _recovery = new RecoveryService(
            new ChangeJournal(paths.RecoveryPath, log),
            () => ModuleLoader.LoadRestorers(paths.ModulesDir, log),
            log,
            clock);
        _modules = new ModuleHost(_events, Data, Texts, _economy, _saves, _settings, _recovery, log, options.DeveloperMode);
    }

    public IEventBus Events => _events;
    public IEconomy Economy => _economy;
    public IKeyValueStore Saves => _saves;
    public IKeyValueStore Settings => _settings;
    public GameData Data { get; }
    public TextCatalog Texts { get; }
    public IReadOnlyList<IModule> RunningModules => _modules.Running;
    public RecoveryService Recovery => _recovery;

    /// <param name="beforeRecovery">
    /// Вызывается, когда настройки уже прочитаны, но модули ещё не запущены: здесь запускается сторожевой процесс.
    /// </param>
    public void Start(Action? beforeRecovery = null)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Directory.CreateDirectory(_paths.UserDir);
        _settings.Load();
        _saves.Load();
        Data.Load();
        _economy.Load();

        var language = _settings.Get(LanguageSettingKey, DefaultLanguage) ?? DefaultLanguage;
        Texts.Use(language);

        beforeRecovery?.Invoke();

        // Изменения прошлого запуска, если сторож не смог их откатить. Модулям нужна чистая система.
        _recovery.RestoreAll();
        // Покупка записывается сразу: после сбоя она не должна пропасть до следующего автосохранения.
        _events.Subscribe<ItemUnlocked>(_ => Save());
        _events.Subscribe<SaveRequested>(_ => Save());
        _modules.Start(ModuleLoader.Load(_paths.ModulesDir, _log));
        _events.Publish(new AppStarted());
        _autosave = new Timer(_ => Save(), null, AutosaveInterval, AutosaveInterval);
        _log.Info($"Ядро запущено, модулей: {_modules.Running.Count}");
    }

    /// <summary>Записывает изменения на диск. Ошибка записи не роняет программу: данные останутся в памяти до следующей попытки.</summary>
    public void Save()
    {
        Flush(_saves);
        Flush(_settings);
    }

    /// <summary>
    /// Сбрасывает весь игровой прогресс: монетки, покупки, улучшения и статистику. Настройки не меняются.
    /// Модули останавливаются, снимают свои эффекты и запускаются заново с пустым сохранением.
    /// Прежнее сохранение остаётся копией рядом, путь к ней возвращается. Вызывается из потока интерфейса.
    /// </summary>
    public string? ResetProgress()
    {
        if (!_started)
        {
            return null;
        }

        _modules.StopAll();
        _recovery.RestoreSession(_recovery.Session);
        Save();

        // Сброс необратим для игры, но не для человека: прошлое сохранение можно вернуть руками.
        string? backup = null;
        if (File.Exists(_paths.SavePath))
        {
            backup = Path.Combine(_paths.UserDir, $"save.before-reset-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Copy(_paths.SavePath, backup, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error("Не удалось сохранить копию перед сбросом", ex);
                backup = null;
            }
        }

        _saves.Clear();
        _economy.Load();
        Save();
        _modules.Start(ModuleLoader.Load(_paths.ModulesDir, _log));
        _log.Warn($"Прогресс сброшен, копия прежнего сохранения: {backup ?? "нет"}");
        _events.Publish(new ProgressReset());
        return backup;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _autosave?.Dispose();
        _autosave = null;
        _events.Publish(new AppStopping());
        _modules.StopAll();

        // Изменения, которые модуль не снял сам, например из-за ошибки при остановке.
        _recovery.RestoreSession(_recovery.Session);
        Save();
        _log.Info("Ядро остановлено");
    }

    public void Dispose() => Stop();

    private void Flush(JsonFileStore store)
    {
        try
        {
            store.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Не удалось записать {store.Path}", ex);
        }
    }
}
