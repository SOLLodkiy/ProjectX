using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Input;

namespace ADHDWindows.Modules.Hardships.Screen;

/// <summary>
/// Налог: каждый второй засчитанный клик не приносит монеток. Модуль кликов сообщает о засчитанном клике
/// (<see cref="ClickCounted"/>) прямо перед начислением, поэтому на время налогового клика в формулу дохода
/// этой кнопки ставится множитель 0, а на следующем клике снимается.
/// </summary>
public sealed class TaxHardship : HardshipModule
{
    // Источники дохода кнопок из модуля кликов.
    private static readonly Dictionary<MouseButton, string> Sources = new()
    {
        [MouseButton.Left] = "click",
        [MouseButton.Right] = "click.right",
        [MouseButton.Middle] = "click.middle",
    };

    private readonly Lock _lock = new();
    private IDisposable? _subscription;
    private IDisposable? _tax;
    private bool _running;
    private int _clicks;
    private int _taxed;

    public override HardshipInfo Info { get; } = new("tax", HardshipLevel.Medium, HardshipResources.None);

    protected override void OnStart() =>
        _subscription = Context.Events.Subscribe<ClickCounted>(OnClick);

    protected override void OnStop() => _subscription?.Dispose();

    protected override void Begin()
    {
        lock (_lock)
        {
            _running = true;
            _clicks = 0;
            _taxed = 0;
        }
    }

    protected override void End()
    {
        lock (_lock)
        {
            _running = false;
            _tax?.Dispose();
            _tax = null;
            Context.Log.Info($"Налог: удержано кликов {_taxed}");
        }
    }

    private void OnClick(ClickCounted click)
    {
        lock (_lock)
        {
            _tax?.Dispose();
            _tax = null;
            if (!_running || !Sources.TryGetValue(click.Button, out var source))
            {
                return;
            }

            _clicks++;
            if (_clicks % 2 == 0)
            {
                _taxed++;
                _tax = Context.Economy.AddModifier(new IncomeModifier(IncomeStep.Hardship, 0, source));
            }
        }
    }
}
