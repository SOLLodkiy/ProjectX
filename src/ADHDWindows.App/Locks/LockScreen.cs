using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;

namespace ADHDWindows.App.Locks;

/// <summary>
/// Связывает блокировку с окном покупки. Создаётся до запуска ядра: иначе первое событие модуля потерялось бы.
/// Окна открываются через очередь интерфейса, то есть после завершения запуска.
/// </summary>
internal sealed class LockScreen : IDisposable
{
    private readonly IEventBus _events;
    private readonly IEconomy _economy;
    private readonly TextCatalog _texts;
    private readonly Func<bool> _designReady;
    private readonly Dispatcher _dispatcher;
    private readonly List<IDisposable> _subscriptions;
    private IReadOnlyList<LockOffer> _offers = [];
    private LockScreenWindow? _window;

    public LockScreen(IEventBus events, IEconomy economy, TextCatalog texts, Func<bool> designReady, Dispatcher dispatcher)
    {
        _events = events;
        _economy = economy;
        _texts = texts;
        _designReady = designReady;
        _dispatcher = dispatcher;
        _subscriptions =
        [
            events.Subscribe<LockOffersChanged>(change => OnUi(() => Update(change.Offers))),
            events.Subscribe<LockScreenRequested>(_ => OnUi(Open)),
            events.Subscribe<UnlockDenied>(_ => OnUi(() => _window?.Refuse())),
            events.Subscribe<ItemUnlocked>(unlocked => OnUi(() => Celebrate(unlocked.Offer))),
            events.Subscribe<CoinsEarned>(_ => OnUi(RefreshBalance)),
            events.Subscribe<CoinsSpent>(_ => OnUi(RefreshBalance)),
        ];
    }

    public bool HasOffers => _offers.Count > 0;

    public void Open()
    {
        if (!_designReady() || _offers.Count == 0)
        {
            return;
        }

        if (_window is null)
        {
            _window = new LockScreenWindow(_texts, key => _events.Publish(new UnlockRequested(key)));
            _window.Closed += (_, _) => _window = null;
            _window.Update(_offers, _economy.Balance);
            _window.Show();
        }
    }

    public void Dispose()
    {
        _subscriptions.ForEach(subscription => subscription.Dispose());
        _window?.Close();
    }

    private void OnUi(Action action) => _dispatcher.BeginInvoke(action);

    private void Update(IReadOnlyList<LockOffer> offers)
    {
        _offers = offers;
        if (offers.Count == 0)
        {
            _window?.Close();
        }
        else
        {
            _window?.Update(offers, _economy.Balance);
        }
    }

    private void RefreshBalance() => _window?.ShowBalance(_economy.Balance);

    private void Celebrate(LockOffer offer)
    {
        if (_designReady())
        {
            ToastManager.Show(ToastKind.Success, _texts.Get("lock.unlocked.title"), offer.Name, "unlock");
        }
    }
}
