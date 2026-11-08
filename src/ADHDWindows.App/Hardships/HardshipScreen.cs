using System.Windows.Forms;
using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;

namespace ADHDWindows.App.Hardships;

/// <summary>
/// Связывает планировщик усложнений с уведомлениями и меню трея. Если модуль усложнений не загружен,
/// меню показывает, что усложнений нет.
/// </summary>
internal sealed class HardshipScreen : IDisposable
{
    private static readonly int[] QuietMinutes = [30, 60, 120];

    private readonly IEventBus _events;
    private readonly TextCatalog _texts;
    private readonly Func<bool> _designReady;
    private readonly Dispatcher _dispatcher;
    private readonly bool _developerMode;
    private readonly List<IDisposable> _subscriptions;
    private volatile HardshipStatus? _latest;

    public HardshipScreen(IEventBus events, TextCatalog texts, Func<bool> designReady, Dispatcher dispatcher, bool developerMode)
    {
        _events = events;
        _texts = texts;
        _designReady = designReady;
        _dispatcher = dispatcher;
        _developerMode = developerMode;
        _subscriptions =
        [
            events.Subscribe<HardshipStatus>(status => _latest = status),
            events.Subscribe<HardshipWarned>(warned => Notify(
                ToastKind.Danger,
                _texts.Get("hardships.warned.title", Title(warned.Hardship), (int)Math.Ceiling(warned.Delay.TotalSeconds)),
                _texts.Get($"hardships.{warned.Hardship.Id}.text"),
                "warning")),
            events.Subscribe<HardshipEnded>(ended =>
            {
                if (ended.Reason == HardshipEndReason.Survived)
                {
                    Notify(
                        ToastKind.Reward,
                        _texts.Get("hardships.survived.title"),
                        _texts.Get("hardships.survived.text", Title(ended.Hardship), CoinFormat.Short(ended.Bonus)),
                        "trophy");
                }
                else if (ended.Reason == HardshipEndReason.Interrupted)
                {
                    Notify(ToastKind.Info, _texts.Get("hardships.interrupted.title"), Title(ended.Hardship), "clock");
                }
            }),
        ];
    }

    public HardshipStatus? Latest => _latest;

    /// <summary>Спрашивает состояние. Модуль отвечает сразу, в этом же потоке.</summary>
    public void Request() => _events.Publish(new HardshipStatusRequested());

    /// <summary>Заполняет подменю трея по последнему состоянию. Вызывается при каждом открытии подменю.</summary>
    public void Fill(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        if (_latest is not { } status)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem(_texts.Get("hardships.menu.missing")) { Enabled = false });
            return;
        }

        menu.DropDownItems.Add(new ToolStripMenuItem(Describe(status)) { Enabled = false });
        menu.DropDownItems.Add(new ToolStripSeparator());

        foreach (var mode in Enum.GetValues<HardshipMode>())
        {
            var item = new ToolStripMenuItem(_texts.Get($"hardships.mode.{mode}")) { Checked = status.Mode == mode };
            item.Click += (_, _) => _events.Publish(new HardshipModeRequested(mode));
            menu.DropDownItems.Add(item);
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        var quiet = new ToolStripMenuItem(_texts.Get("hardships.menu.quiet")) { Checked = status.QuietUntil is not null };
        foreach (var minutes in QuietMinutes)
        {
            quiet.DropDownItems.Add(_texts.Get("hardships.menu.quietFor", minutes), null, (_, _) =>
                _events.Publish(new HardshipQuietRequested(TimeSpan.FromMinutes(minutes))));
        }

        if (status.QuietUntil is not null)
        {
            quiet.DropDownItems.Add(_texts.Get("hardships.menu.quietOff"), null, (_, _) =>
                _events.Publish(new HardshipQuietRequested(TimeSpan.Zero)));
        }

        menu.DropDownItems.Add(quiet);

        var list = new ToolStripMenuItem(_texts.Get("hardships.menu.list"));
        foreach (var entry in status.Hardships)
        {
            var id = entry.Hardship.Id;
            var item = new ToolStripMenuItem($"{Title(entry.Hardship)} ({_texts.Get($"quests.tier.{entry.Hardship.Level}")})")
            {
                Checked = entry.Enabled,
                CheckOnClick = true,
            };
            item.CheckedChanged += (_, _) => _events.Publish(new HardshipToggleRequested(id, item.Checked));
            list.DropDownItems.Add(item);
        }

        menu.DropDownItems.Add(list);

        if (status.Active is not null)
        {
            menu.DropDownItems.Add(_texts.Get("hardships.menu.cancel"), null, (_, _) => _events.Publish(new HardshipCancelRequested()));
        }

        if (_developerMode)
        {
            var test = new ToolStripMenuItem(_texts.Get("hardships.menu.test"));
            foreach (var entry in status.Hardships)
            {
                var id = entry.Hardship.Id;
                test.DropDownItems.Add(Title(entry.Hardship), null, (_, _) => _events.Publish(new HardshipTestRequested(id)));
            }

            menu.DropDownItems.Add(new ToolStripSeparator());
            menu.DropDownItems.Add(test);
        }
    }

    public void Dispose() => _subscriptions.ForEach(subscription => subscription.Dispose());

    private string Describe(HardshipStatus status)
    {
        var multiplier = _texts.Get("hardships.menu.income", status.Multiplier.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
        if (status.Active is { } active)
        {
            return $"{_texts.Get("hardships.menu.active", Title(active))} · {multiplier}";
        }

        if (status.Blocked is { } blocked)
        {
            return $"{_texts.Get($"hardships.blocked.{blocked}")} · {multiplier}";
        }

        return status.Next is { } next
            ? $"{_texts.Get("hardships.menu.next", next.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture))} · {multiplier}"
            : multiplier;
    }

    private string Title(HardshipInfo info) => _texts.Get($"hardships.{info.Id}.title");

    private void Notify(ToastKind kind, string title, string text, string icon) => _dispatcher.BeginInvoke(() =>
    {
        if (_designReady())
        {
            ToastManager.Show(kind, title, text, icon);
        }
    });
}
