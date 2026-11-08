using System.Collections.Frozen;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Upgrades;

namespace ADHDWindows.Modules.Clicks;

/// <summary>
/// Монетки за нажатия кнопок мыши. ЛКМ приносит доход всегда, ПКМ и средняя кнопка — после покупки улучшения
/// с эффектом <see cref="ButtonEffect"/>. У каждой кнопки свой источник дохода и своя защита от автокликеров.
/// </summary>
public sealed class ClicksModule : IModule
{
    public const string IncomeSource = "click";
    public const string RightSource = "click.right";
    public const string MiddleSource = "click.middle";
    public const decimal BaseIncome = 1;

    /// <summary>Эффект улучшения: кнопка из параметра <c>button</c> (<c>right</c> или <c>middle</c>) начинает приносить монетки.</summary>
    public const string ButtonEffect = "clicks.button";

    private static readonly FrozenDictionary<MouseButton, (string Source, string Code)> Buttons = new Dictionary<MouseButton, (string, string)>
    {
        [MouseButton.Left] = (IncomeSource, "left"),
        [MouseButton.Right] = (RightSource, "right"),
        [MouseButton.Middle] = (MiddleSource, "middle"),
    }.ToFrozenDictionary();

    private readonly Dictionary<MouseButton, ClickFilter> _filters = Buttons.Keys.ToDictionary(button => button, button => new ClickFilter(button));
    private readonly Dictionary<MouseButton, ClickVerdict> _lastRejection = Buttons.Keys.ToDictionary(button => button, _ => ClickVerdict.Counted);

    // Меняется из потока покупки, читается в потоке хука, поэтому заменяется целиком.
    private volatile FrozenSet<MouseButton> _paid = FrozenSet.ToFrozenSet([MouseButton.Left]);

    public string Id => "clicks";

    public string Name => "Монетки за клики";

    public void Start(IModuleContext context)
    {
        context.Events.Subscribe<MouseButtonChanged>(change => OnButton(context, change));
        context.Events.Subscribe<UpgradesChanged>(change => OnUpgrades(change.Owned));

        // Магазин мог запуститься раньше и уже разослать купленные улучшения.
        context.Events.Publish(new UpgradesRequested());
    }

    public void Stop()
    {
    }

    private void OnUpgrades(IReadOnlyList<UpgradeLevel> owned) =>
        _paid = Buttons
            .Where(pair => pair.Key == MouseButton.Left || UpgradeEffects.Has(owned, ButtonEffect, "button", pair.Value.Code))
            .Select(pair => pair.Key)
            .ToFrozenSet();

    private void OnButton(IModuleContext context, MouseButtonChanged change)
    {
        if (!_filters.TryGetValue(change.Button, out var filter))
        {
            return;
        }

        // Фильтр видит и неоплаченные нажатия: после покупки автокликер не получит чистую историю.
        var verdict = filter.Process(change);
        if (verdict == ClickVerdict.Counted)
        {
            context.Events.Publish(new ClickCounted(change.Button, change.Time));
        }

        switch (verdict)
        {
            case ClickVerdict.Counted when _paid.Contains(change.Button):
                context.Economy.Earn(Buttons[change.Button].Source, BaseIncome);
                _lastRejection[change.Button] = ClickVerdict.Counted;
                break;
            case ClickVerdict.TooFast or ClickVerdict.TooEven when verdict != _lastRejection[change.Button]:
                // Каждый отклонённый клик в журнал не пишется: автокликер заполнил бы его за минуты.
                context.Log.Warn($"Клики {Buttons[change.Button].Code} не засчитываются: {verdict}");
                _lastRejection[change.Button] = verdict;
                break;
        }
    }
}
