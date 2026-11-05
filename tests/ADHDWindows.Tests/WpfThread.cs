using System.Windows;
using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;

namespace ADHDWindows.Tests;

/// <summary>Общий поток интерфейса для тестов WPF: Application в процессе может быть только один.</summary>
internal static class WpfThread
{
    private static readonly Lazy<Dispatcher> Instance = new(Start);

    public static void Run(Action action) => Instance.Value.Invoke(action);

    private static Dispatcher Start()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var root = RepositoryRoot.Find();
            var data = new GameData(Path.Combine(root, "data"), new RecordingLog());
            data.Load();
            var texts = new TextCatalog(data, "ru", new RecordingLog());
            texts.Use("ru");
            PixelAssets.Install(application.Resources, Path.Combine(root, "assets"), texts);

            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
