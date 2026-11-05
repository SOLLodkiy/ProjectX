using ADHDWindows.Core.Events;
using ADHDWindows.Core.Modules;

namespace ADHDWindows.Modules.Sample;

/// <summary>Тестовый модуль: считает запуски, чтобы проверить загрузку, шину и сохранения.</summary>
public sealed class SampleModule : IModule
{
    public const string RunsKey = "runs";

    public string Id => "sample";

    public string Name => "Тестовый модуль";

    public void Start(IModuleContext context)
    {
        var runs = context.Saves.Get(RunsKey, 0) + 1;
        context.Saves.Set(RunsKey, runs);
        context.Events.Subscribe<AppStarted>(_ => context.Log.Info($"Запуск номер {runs}"));
    }

    public void Stop()
    {
    }
}
