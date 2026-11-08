using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Quests;

namespace ADHDWindows.App.Quests;

/// <summary>
/// Связывает модуль заданий с окном и уведомлениями. Создаётся до запуска ядра, чтобы не пропустить первые задания.
/// Если модуль заданий не загружен, заданий нет и окно не открывается.
/// </summary>
internal sealed class QuestScreen : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly IEventBus _events;
    private readonly TextCatalog _texts;
    private readonly Func<bool> _designReady;
    private readonly Dispatcher _dispatcher;
    private readonly List<IDisposable> _subscriptions;
    private readonly DispatcherTimer _timer;
    private volatile QuestsChanged? _latest;
    private QuestsWindow? _window;

    public QuestScreen(IEventBus events, TextCatalog texts, Func<bool> designReady, Dispatcher dispatcher)
    {
        _events = events;
        _texts = texts;
        _designReady = designReady;
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, (_, _) => _window?.Tick(DateTimeOffset.UtcNow), dispatcher) { IsEnabled = false };
        _subscriptions =
        [
            events.Subscribe<QuestsChanged>(change =>
            {
                _latest = change;
                OnUi(() => _window?.Update(change.Quests, DateTimeOffset.UtcNow, change.Chain, change.ChainGoal));
            }),
            events.Subscribe<QuestIssued>(issued => OnUi(() => Notify(
                issued.Notify,
                ToastKind.Info,
                _texts.Get("quests.issued.title"),
                _texts.Get("quests.issued.text", _texts.Get($"quests.tier.{issued.Quest.Tier}"), issued.Quest.Title),
                "quest"))),
            events.Subscribe<QuestCompleted>(completed => OnUi(() =>
            {
                _window?.Celebrate(completed.Quest.Tier);
                Notify(
                    completed.Notify,
                    ToastKind.Reward,
                    _texts.Get("quests.completed.title"),
                    _texts.Get("quests.completed.text", completed.Quest.Title, CoinFormat.Short(completed.Earned)),
                    "coin");
            })),
        ];
    }

    /// <summary>Модуль заданий ответил хотя бы раз.</summary>
    public bool Available => _latest is not null;

    internal QuestsWindow? Window => _window;

    /// <summary>Спрашивает задания. Модуль отвечает сразу, в этом же потоке.</summary>
    public void Request() => _events.Publish(new QuestsRequested());

    public void Open()
    {
        if (!_designReady() || _latest is not { } latest)
        {
            return;
        }

        if (_window is null)
        {
            _window = new QuestsWindow(_texts);
            _window.Closed += (_, _) =>
            {
                _window = null;
                _timer.Stop();
            };
            _window.Update(latest.Quests, DateTimeOffset.UtcNow, latest.Chain, latest.ChainGoal);
            _window.Show();
            _timer.Start();
        }

        _window.Activate();
    }

    public void Dispose()
    {
        _subscriptions.ForEach(subscription => subscription.Dispose());
        _timer.Stop();
        _window?.Close();
    }

    private void OnUi(Action action) => _dispatcher.BeginInvoke(action);

    private void Notify(bool enabled, ToastKind kind, string title, string text, string icon)
    {
        if (enabled && _designReady())
        {
            ToastManager.Show(kind, title, text, icon);
        }
    }
}
