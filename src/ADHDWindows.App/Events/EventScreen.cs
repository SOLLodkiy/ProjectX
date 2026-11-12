using System.Globalization;
using System.Windows.Forms;
using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.RandomEvents;

namespace ADHDWindows.App.Events;

/// <summary>
/// Связывает случайные события с уведомлениями, окнами событий и меню трея. Если модуль событий не загружен,
/// меню показывает, что событий нет.
/// </summary>
internal sealed class EventScreen : IDisposable
{
    private static readonly TimeSpan ChestToastWindow = TimeSpan.FromSeconds(3);

    private readonly IEventBus _events;
    private readonly IEconomy _economy;
    private readonly TextCatalog _texts;
    private readonly Func<bool> _designReady;
    private readonly Dispatcher _dispatcher;
    private readonly bool _developerMode;
    private readonly List<IDisposable> _subscriptions;
    private readonly Dictionary<string, EventDialogWindow> _dialogs = new(StringComparer.Ordinal);
    private volatile RandomEventStatus? _latest;
    private volatile bool _hardshipActive;
    private DateTimeOffset _lastChest;

    public EventScreen(IEventBus events, IEconomy economy, TextCatalog texts, Func<bool> designReady, Dispatcher dispatcher, bool developerMode)
    {
        _events = events;
        _economy = economy;
        _texts = texts;
        _designReady = designReady;
        _dispatcher = dispatcher;
        _developerMode = developerMode;
        _subscriptions =
        [
            events.Subscribe<RandomEventStatus>(status => _latest = status),
            events.Subscribe<HardshipStatus>(status => _hardshipActive = status.Active is not null),
            events.Subscribe<RandomEventStarted>(started => Notify(
                ToastKind.Info,
                Title(started.Event),
                _texts.Get($"events.{started.Event.Id}.text"),
                "star")),
            events.Subscribe<RandomEventEnded>(ended => OnUi(() =>
            {
                if (ended.Reason == RandomEventEndReason.Completed && ended.Reward > 0 && DateTimeOffset.UtcNow - _lastChest > ChestToastWindow)
                {
                    Show(ToastKind.Reward, Title(ended.Event), _texts.Get("events.ended.reward", CoinFormat.Short(ended.Reward)), "coin");
                }
            })),
            events.Subscribe<RandomEventNotice>(notice => Notify(notice.Reward ? ToastKind.Reward : ToastKind.Info, notice.Title, notice.Text, notice.Icon)),
            events.Subscribe<ChestOpened>(opened => OnUi(() =>
            {
                _lastChest = DateTimeOffset.UtcNow;
                Show(
                    ToastKind.Reward,
                    _texts.Get("events.chest.opened", _texts.Get($"events.chest.tier.{opened.Tier}")),
                    opened.Items.Count == 0 ? _texts.Get("events.chest.empty") : string.Join(", ", opened.Items.Select(item => item.Text)),
                    "chest");
            })),
            events.Subscribe<LockSaleStarted>(sale => Notify(
                ToastKind.Reward,
                _texts.Get("events.sale.toast.title", sale.Percent),
                _texts.Get(
                    "events.sale.toast.text",
                    sale.Offer.Name,
                    CoinFormat.Short(sale.SalePrice),
                    CoinFormat.Short(sale.Offer.Price),
                    sale.Until.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)),
                "shop")),
            events.Subscribe<EventDialogShown>(shown => OnUi(() => ShowDialog(shown.Dialog))),
            events.Subscribe<EventDialogClosed>(closed => OnUi(() =>
            {
                if (_dialogs.Remove(closed.Id, out var window))
                {
                    window.CloseByEvent();
                }
            })),
        ];
    }

    public RandomEventStatus? Latest => _latest;

    /// <summary>Открытые окна событий, для проверок.</summary>
    internal IReadOnlyCollection<EventDialogWindow> Dialogs => _dialogs.Values;

    public void Request() => _events.Publish(new RandomEventStatusRequested());

    /// <summary>Заполняет подменю трея «События» по последнему состоянию.</summary>
    public void Fill(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        if (_latest is not { } status)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem(_texts.Get("events.menu.missing")) { Enabled = false });
            return;
        }

        menu.DropDownItems.Add(new ToolStripMenuItem(Describe(status)) { Enabled = false });
        foreach (var entry in status.Events.Where(entry => entry.Event.Manual))
        {
            var id = entry.Event.Id;
            menu.DropDownItems.Add(new ToolStripMenuItem(_texts.Get($"events.{id}.open"), null, (_, _) =>
                Task.Run(() => _events.Publish(new RandomEventOpenRequested(id))))
            {
                Enabled = status.Active is null && !_hardshipActive,
            });
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var frequency in Enum.GetValues<RandomEventFrequency>())
        {
            var item = new ToolStripMenuItem(_texts.Get($"events.frequency.{frequency}")) { Checked = status.Frequency == frequency };
            item.Click += (_, _) => _events.Publish(new RandomEventFrequencyRequested(frequency));
            menu.DropDownItems.Add(item);
        }

        var list = new ToolStripMenuItem(_texts.Get("events.menu.list"));
        foreach (var entry in status.Events)
        {
            var id = entry.Event.Id;
            var item = new ToolStripMenuItem(Title(entry.Event)) { Checked = entry.Enabled, CheckOnClick = true };
            item.CheckedChanged += (_, _) => _events.Publish(new RandomEventToggleRequested(id, item.Checked));
            list.DropDownItems.Add(item);
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(list);

        if (_developerMode)
        {
            var test = new ToolStripMenuItem(_texts.Get("events.menu.test"));
            foreach (var entry in status.Events)
            {
                var id = entry.Event.Id;
                if (id == "chest")
                {
                    var chest = new ToolStripMenuItem(Title(entry.Event));
                    foreach (var tier in ChestTiers.All)
                    {
                        chest.DropDownItems.Add(_texts.Get($"events.chest.tier.{tier}"), null, (_, _) => Test(id, tier));
                    }

                    test.DropDownItems.Add(chest);
                    continue;
                }

                test.DropDownItems.Add(Title(entry.Event), null, (_, _) => Test(id, null));
            }

            menu.DropDownItems.Add(test);
        }
    }

    public void Dispose()
    {
        _subscriptions.ForEach(subscription => subscription.Dispose());
        foreach (var window in _dialogs.Values.ToList())
        {
            window.CloseByEvent();
        }

        _dialogs.Clear();
    }

    /// <summary>Модули событий отвечают из своего потока, поэтому запуск не задерживает интерфейс.</summary>
    private void Test(string id, string? variant) => Task.Run(() => _events.Publish(new RandomEventTestRequested(id, variant)));

    private void ShowDialog(EventDialog dialog)
    {
        if (!_designReady())
        {
            return;
        }

        if (_dialogs.TryGetValue(dialog.Id, out var window))
        {
            window.Update(dialog);
            return;
        }

        var id = dialog.Id;
        window = new EventDialogWindow(_texts, _economy, dialog, choice =>
        {
            if (choice == EventDialogChosen.CloseChoice)
            {
                _dialogs.Remove(id);
            }

            _events.Publish(new EventDialogChosen(id, choice));
        });
        _dialogs[id] = window;
        window.Show();
    }

    private string Describe(RandomEventStatus status)
    {
        if (status.Active is { } active)
        {
            return _texts.Get("events.menu.active", Title(active));
        }

        if (status.Blocked is { } blocked)
        {
            return _texts.Get($"events.blocked.{blocked}");
        }

        return status.Next is { } next
            ? _texts.Get("events.menu.next", next.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture))
            : _texts.Get("events.frequency.Off");
    }

    private string Title(RandomEventInfo info) => _texts.Get($"events.{info.Id}.title");

    private void OnUi(Action action) => _dispatcher.BeginInvoke(action);

    private void Notify(ToastKind kind, string title, string text, string icon) => OnUi(() => Show(kind, title, text, icon));

    private void Show(ToastKind kind, string title, string text, string icon)
    {
        if (_designReady())
        {
            ToastManager.Show(kind, title, text, icon);
        }
    }
}
