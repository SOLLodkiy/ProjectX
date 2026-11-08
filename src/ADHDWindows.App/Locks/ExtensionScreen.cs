using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;

namespace ADHDWindows.App.Locks;

/// <summary>Связывает наблюдение за браузерами с окном «Нужно расширение». Создаётся до запуска ядра, как экран покупки.</summary>
internal sealed class ExtensionScreen : IDisposable
{
    private readonly IEventBus _events;
    private readonly TextCatalog _texts;
    private readonly Func<bool> _designReady;
    private readonly Dispatcher _dispatcher;
    private readonly List<IDisposable> _subscriptions;
    private IReadOnlyList<ExtensionProblem> _problems = [];
    private ExtensionScreenWindow? _window;

    public ExtensionScreen(IEventBus events, TextCatalog texts, Func<bool> designReady, Dispatcher dispatcher)
    {
        _events = events;
        _texts = texts;
        _designReady = designReady;
        _dispatcher = dispatcher;
        _subscriptions =
        [
            events.Subscribe<ExtensionProblemsChanged>(change => OnUi(() => Update(change.Problems))),
            events.Subscribe<ExtensionScreenRequested>(_ => OnUi(Open)),
        ];
    }

    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "browser-extension");

    public void Open()
    {
        if (!_designReady() || _problems.Count == 0 || _window is not null)
        {
            return;
        }

        _window = new ExtensionScreenWindow(_texts, id => _events.Publish(new ExtensionPageRequested(id)), OpenFolder);
        _window.Closed += (_, _) => _window = null;
        _window.Update(_problems);
        _window.Show();
    }

    public void Dispose()
    {
        _subscriptions.ForEach(subscription => subscription.Dispose());
        _window?.Close();
    }

    private static void OpenFolder()
    {
        if (Directory.Exists(ExtensionFolder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExtensionFolder}\"") { UseShellExecute = false })?.Dispose();
        }
    }

    private void OnUi(Action action) => _dispatcher.BeginInvoke(action);

    private void Update(IReadOnlyList<ExtensionProblem> problems)
    {
        _problems = problems;
        if (problems.Count == 0)
        {
            _window?.Close();
        }
        else
        {
            _window?.Update(problems);
        }
    }
}
