using ADHDWindows.Core.Events;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Safety;

namespace ADHDWindows.Modules.Sample;

/// <summary>Тестовый модуль: проверяет загрузку, шину, сохранения и откат изменений системы.</summary>
public sealed class SampleModule : IModule
{
    public const string RunsKey = "runs";

    private readonly Dictionary<string, IDisposable> _probes = [];
    private readonly SampleProbe _probe = new();

    public string Id => "sample";

    public string Name => "Тестовый модуль";

    public void Start(IModuleContext context)
    {
        var runs = context.Saves.Get(RunsKey, 0) + 1;
        context.Saves.Set(RunsKey, runs);
        context.Events.Subscribe<AppStarted>(_ => context.Log.Info($"Запуск номер {runs}"));
        if (context.DeveloperMode)
        {
            context.Events.Subscribe<RecoveryProbeRequested>(request => ApplyProbe(context, request.Token));
        }
    }

    public void Stop()
    {
        foreach (var (token, mark) in _probes)
        {
            _probe.Restore(token);
            mark.Dispose();
        }

        _probes.Clear();
    }

    private void ApplyProbe(IModuleContext context, string token)
    {
        if (_probes.ContainsKey(token))
        {
            return;
        }

        _probes[token] = context.Changes.Record(SampleProbe.ChangeKind, token);
        SampleProbe.Apply(token);
        context.Log.Info($"Тестовое изменение сделано: {token}");
    }
}
