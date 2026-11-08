using System.Windows.Threading;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Upgrades;

namespace ADHDWindows.App.Shop;

/// <summary>
/// Связывает магазин улучшений с его окном. Создаётся до запуска ядра, чтобы не пропустить первое состояние магазина.
/// Если модуль магазина не загружен, состояния нет и окно не открывается.
/// </summary>
internal sealed class ShopScreen : IDisposable
{
    private readonly IEventBus _events;
    private readonly IEconomy _economy;
    private readonly TextCatalog _texts;
    private readonly Func<bool> _designReady;
    private readonly Dispatcher _dispatcher;
    private readonly List<IDisposable> _subscriptions;
    private IReadOnlyList<UpgradeShopBranch>? _branches;
    private ShopWindow? _window;

    public ShopScreen(IEventBus events, IEconomy economy, TextCatalog texts, Func<bool> designReady, Dispatcher dispatcher)
    {
        _events = events;
        _economy = economy;
        _texts = texts;
        _designReady = designReady;
        _dispatcher = dispatcher;
        _subscriptions =
        [
            events.Subscribe<UpgradeShopChanged>(change => OnUi(() => Update(change.Branches))),
            events.Subscribe<UpgradePurchased>(purchased => OnUi(() => _window?.Celebrate(purchased.Id))),
            events.Subscribe<UpgradePurchaseDenied>(denied => OnUi(() => _window?.Refuse(denied.Id))),
            events.Subscribe<CoinsEarned>(_ => OnUi(RefreshBalance)),
            events.Subscribe<CoinsSpent>(_ => OnUi(RefreshBalance)),
        ];
    }

    /// <summary>Магазин загружен, и окно можно открыть.</summary>
    public bool Available => _branches is not null;

    /// <summary>Окно, если оно открыто, для проверок.</summary>
    internal ShopWindow? Window => _window;

    public void Open()
    {
        if (!_designReady() || _branches is null)
        {
            return;
        }

        if (_window is null)
        {
            _window = new ShopWindow(_texts, id => _events.Publish(new UpgradePurchaseRequested(id)));
            _window.Closed += (_, _) => _window = null;
            _window.Update(_branches, _economy.Balance);
            _window.Show();
        }

        _window.Activate();
    }

    public void Dispose()
    {
        _subscriptions.ForEach(subscription => subscription.Dispose());
        _window?.Close();
    }

    private void OnUi(Action action) => _dispatcher.BeginInvoke(action);

    private void Update(IReadOnlyList<UpgradeShopBranch> branches)
    {
        _branches = branches;
        _window?.Update(branches, _economy.Balance);
    }

    private void RefreshBalance() => _window?.ShowBalance(_economy.Balance);
}
