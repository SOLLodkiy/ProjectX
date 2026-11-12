using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Progress;

namespace ADHDWindows.App.Progress;

/// <summary>
/// Связывает модуль прогресса с окном, итогом дня и уведомлениями. Создаётся до запуска ядра, чтобы не пропустить
/// награду серии и итог дня при запуске. Если модуль прогресса не загружен, окно не открывается.
/// </summary>
internal sealed class ProgressScreen : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly IEventBus _events;
    private readonly TextCatalog _texts;
    private readonly Func<bool> _designReady;
    private readonly Dispatcher _dispatcher;
    private readonly List<IDisposable> _subscriptions;
    private readonly DispatcherTimer _timer;
    private volatile ProgressSnapshot? _latest;
    private ProgressWindow? _window;
    private DaySummaryWindow? _summary;
    private DaySummary? _pendingSummary;

    public ProgressScreen(IEventBus events, TextCatalog texts, Func<bool> designReady, Dispatcher dispatcher)
    {
        _events = events;
        _texts = texts;
        _designReady = designReady;
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => Request(), dispatcher) { IsEnabled = false };
        _subscriptions =
        [
            events.Subscribe<ProgressChanged>(change =>
            {
                _latest = change.Snapshot;
                OnUi(() => _window?.Update(change.Snapshot));
            }),
            events.Subscribe<LevelReached>(reached => OnUi(() =>
            {
                _window?.Celebrate();
                Notify(
                    reached.Notify,
                    ToastKind.Reward,
                    _texts.Get(reached.RankChanged ? "progress.rank.toast.title" : "progress.level.toast.title", reached.Level),
                    _texts.Get(reached.RankChanged ? "progress.rank.toast.text" : "progress.level.toast.text", reached.RankName, CoinFormat.Short(reached.Earned)),
                    "crown");
            })),
            events.Subscribe<AchievementUnlocked>(unlocked => OnUi(() =>
            {
                _window?.Celebrate();
                Notify(
                    unlocked.Notify,
                    ToastKind.Reward,
                    _texts.Get("progress.achievement.toast.title"),
                    _texts.Get("progress.achievement.toast.text", unlocked.Achievement.Title, CoinFormat.Short(unlocked.Earned)),
                    unlocked.Achievement.Icon);
            })),
            events.Subscribe<StreakRewarded>(rewarded => OnUi(() => Notify(
                rewarded.Notify,
                ToastKind.Reward,
                _texts.Get("progress.streak.toast.title", rewarded.Days),
                _texts.Get(
                    rewarded.SkipUsed ? "progress.streak.toast.skip" : rewarded.Broken ? "progress.streak.toast.broken" : "progress.streak.toast.text",
                    CoinFormat.Short(rewarded.Earned)),
                "flame"))),
            events.Subscribe<WeeklyGoalCompleted>(completed => OnUi(() => Notify(
                completed.Notify,
                ToastKind.Reward,
                _texts.Get("progress.weekly.toast.title"),
                _texts.Get("progress.weekly.toast.text", completed.Goal.Title, CoinFormat.Short(completed.Earned)),
                completed.Goal.Icon))),
            events.Subscribe<DaySummaryReady>(ready =>
            {
                if (ready.Show)
                {
                    OnUi(() => ShowSummary(ready.Summary));
                }
            }),
        ];
    }

    /// <summary>Модуль прогресса ответил хотя бы раз.</summary>
    public bool Available => _latest is not null;

    internal ProgressWindow? Window => _window;

    internal DaySummaryWindow? SummaryWindow => _summary;

    /// <summary>Спрашивает прогресс. Модуль отвечает сразу, в этом же потоке.</summary>
    public void Request() => _events.Publish(new ProgressRequested());

    public void Open()
    {
        if (!_designReady())
        {
            return;
        }

        Request();
        if (_latest is not { } snapshot)
        {
            return;
        }

        if (_window is null)
        {
            _window = new ProgressWindow(_texts, (key, value) => _events.Publish(new ProgressSettingRequested(key, value)));
            _window.Closed += (_, _) =>
            {
                _timer.Stop();
                _window = null;
            };
            _window.Update(snapshot);
            _window.Show();
            _timer.Start();
        }

        _window.Activate();
    }

    /// <summary>Итог, пришедший до загрузки дизайн-системы, показывается, когда она готова.</summary>
    public void ShowPending()
    {
        if (_pendingSummary is { } pending)
        {
            ShowSummary(pending);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _subscriptions.ForEach(subscription => subscription.Dispose());
        _window?.Close();
        _summary?.Close();
    }

    private void ShowSummary(DaySummary summary)
    {
        if (!_designReady())
        {
            _pendingSummary = summary;
            return;
        }

        _pendingSummary = null;
        _summary?.Close();
        _summary = new DaySummaryWindow(_texts, summary);
        _summary.Closed += (_, _) => _summary = null;
        _summary.Show();
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
