using System.IO;
using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Stats;
using Microsoft.Win32;

namespace ADHDWindows.App.Stats;

/// <summary>
/// Связывает модуль статистики с окном «Логи». Пока окно открыто, свежая статистика запрашивается каждые пару секунд.
/// Если модуль статистики не загружен, ответа нет и окно не открывается.
/// </summary>
internal sealed class StatsScreen : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly IEventBus _events;
    private readonly TextCatalog _texts;
    private readonly Func<bool> _designReady;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string?> _resetProgress;
    private readonly ILog _log;
    private readonly List<IDisposable> _subscriptions;
    private readonly DispatcherTimer _timer;
    private volatile StatsSnapshot? _latest;
    private StatsWindow? _window;

    /// <param name="resetProgress">Сбрасывает прогресс и возвращает путь к копии прежнего сохранения.</param>
    public StatsScreen(IEventBus events, TextCatalog texts, Func<bool> designReady, Dispatcher dispatcher, Func<string?> resetProgress, ILog log)
    {
        _events = events;
        _texts = texts;
        _designReady = designReady;
        _dispatcher = dispatcher;
        _resetProgress = resetProgress;
        _log = log;
        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => Request(), dispatcher) { IsEnabled = false };
        _subscriptions =
        [
            events.Subscribe<StatsChanged>(change =>
            {
                _latest = change.Snapshot;
                OnUi(() => _window?.Update(change.Snapshot));
            }),
        ];
    }

    /// <summary>Модуль статистики ответил хотя бы раз.</summary>
    public bool Available => _latest is not null;

    internal StatsWindow? Window => _window;

    /// <summary>Спрашивает статистику. Модуль отвечает сразу, в этом же потоке.</summary>
    public void Request() => _events.Publish(new StatsRequested());

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
            _window = new StatsWindow(_texts, Export, ConfirmReset);
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

    public void Dispose()
    {
        _timer.Stop();
        _subscriptions.ForEach(subscription => subscription.Dispose());
        _window?.Close();
    }

    private void OnUi(Action action) => _dispatcher.BeginInvoke(action);

    private void Export()
    {
        if (_latest is not { } snapshot)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = StatsExport.DefaultFileName(snapshot.Taken),
            DefaultExt = ".json",
            Filter = "JSON (*.json)|*.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(_window) != true)
        {
            return;
        }

        try
        {
            StatsExport.Write(snapshot, dialog.FileName);
            _log.Info($"Статистика сохранена: {dialog.FileName}");
            ToastManager.Show(ToastKind.Success, _texts.Get("stats.exported.title"), _texts.Get("stats.exported.text", dialog.FileName), "chart");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Error("Не удалось сохранить статистику", exception);
            ToastManager.Show(ToastKind.Danger, _texts.Get("stats.exportFailed.title"), _texts.Get("stats.exportFailed.text", exception.Message));
        }
    }

    private void ConfirmReset()
    {
        var confirm = new ResetWindow(_texts) { Owner = _window };
        if (confirm.ShowDialog() != true)
        {
            return;
        }

        var backup = _resetProgress();
        _events.Publish(new FeedPosted("warning", _texts.Get("feed.reset")));
        ToastManager.Show(ToastKind.Info, _texts.Get("reset.done.title"), _texts.Get("reset.done.text", backup ?? "-"));
        Request();
    }
}
