using System.Windows.Threading;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Media;

namespace ADHDWindows.App.Time;

/// <summary>Показывает вопрос «Ты тут?», пока хронометраж ждёт ответа. Создаётся до запуска ядра, как экран покупки.</summary>
internal sealed class PresenceScreen : IDisposable
{
    private readonly IEventBus _events;
    private readonly TextCatalog _texts;
    private readonly Func<bool> _designReady;
    private readonly Dispatcher _dispatcher;
    private readonly IDisposable _subscription;
    private PresenceWindow? _window;

    public PresenceScreen(IEventBus events, TextCatalog texts, Func<bool> designReady, Dispatcher dispatcher)
    {
        _events = events;
        _texts = texts;
        _designReady = designReady;
        _dispatcher = dispatcher;
        _subscription = events.Subscribe<PresenceCheckChanged>(change => _dispatcher.BeginInvoke(() => Update(change)));
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _window?.Close();
    }

    private void Update(PresenceCheckChanged change)
    {
        if (!change.Waiting)
        {
            _window?.Close();
            return;
        }

        if (!_designReady() || _window is not null)
        {
            return;
        }

        _window = new PresenceWindow(_texts, change.Activity, () => _events.Publish(new PresenceConfirmed()));
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }
}
