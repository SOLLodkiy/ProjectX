using ADHDWindows.Core.Data;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Modules;
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
    private readonly ModuleHost _modules;
    private Timer? _autosave;
    private bool _started;

    public GameHost(AppPaths paths, AppOptions options, ILog log)
    {
        _paths = paths;
        _log = log;
        _saves = new JsonFileStore(paths.SavePath, log);
        _settings = new JsonFileStore(paths.SettingsPath, log);
        _events = new EventBus(log);
        Data = new GameData(paths.DataDir, log);
        Texts = new TextCatalog(Data, DefaultLanguage, log);
        _modules = new ModuleHost(_events, Data, Texts, _saves, _settings, log, options.DeveloperMode);
    }

    public IEventBus Events => _events;
    public IKeyValueStore Saves => _saves;
    public IKeyValueStore Settings => _settings;
    public GameData Data { get; }
    public TextCatalog Texts { get; }
    public IReadOnlyList<IModule> RunningModules => _modules.Running;

    public void Start()
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

        var language = _settings.Get(LanguageSettingKey, DefaultLanguage) ?? DefaultLanguage;
        Texts.Use(language);

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
