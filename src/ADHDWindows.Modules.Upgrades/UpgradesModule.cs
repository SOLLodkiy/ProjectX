using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Upgrades;

namespace ADHDWindows.Modules.Upgrades;

/// <summary>
/// Магазин постоянных улучшений. Сами эффекты применяют модули, которые их понимают: они подписаны на
/// <see cref="UpgradesChanged"/>. Общие эффекты дохода из <see cref="UpgradeEffects"/> применяет этот модуль.
/// </summary>
public sealed class UpgradesModule : IModule
{
    private IncomeEffects? _income;

    public string Id => "upgrades";

    public string Name => "Магазин улучшений";

    public void Start(IModuleContext context)
    {
        var branches = UpgradeCatalog.Load(context.Data, context.Log);
        var shop = new UpgradeShop(branches, context.Economy, context.Events, context.Saves, TimeProvider.System, context.Log);
        _income = new IncomeEffects(context.Economy);

        context.Events.Subscribe<UpgradesChanged>(change => _income.Apply(change.Owned));
        context.Events.Subscribe<UpgradePurchaseRequested>(request => shop.Buy(request.Id));
        context.Events.Subscribe<UpgradesRequested>(_ => shop.Publish());

        context.Log.Info($"Улучшений в магазине: {branches.Sum(branch => branch.Upgrades.Count)}, куплено: {shop.Owned().Count}");
        shop.Publish();
    }

    public void Stop()
    {
        _income?.Dispose();
        _income = null;
    }
}
