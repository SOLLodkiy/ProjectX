using ADHDWindows.Core.Input;
using ADHDWindows.Core.Modules;

namespace ADHDWindows.Modules.Clicks;

/// <summary>Первый источник дохода: монетка за нажатие левой кнопки мыши.</summary>
public sealed class ClicksModule : IModule
{
    public const string IncomeSource = "click";
    public const decimal BaseIncome = 1;

    private readonly ClickFilter _filter = new();
    private ClickVerdict _lastRejection = ClickVerdict.Counted;

    public string Id => "clicks";

    public string Name => "Монетки за клики";

    public void Start(IModuleContext context) =>
        context.Events.Subscribe<MouseButtonChanged>(change => OnButton(context, change));

    public void Stop()
    {
    }

    private void OnButton(IModuleContext context, MouseButtonChanged change)
    {
        var verdict = _filter.Process(change);
        switch (verdict)
        {
            case ClickVerdict.Counted:
                context.Economy.Earn(IncomeSource, BaseIncome);
                _lastRejection = ClickVerdict.Counted;
                break;
            case ClickVerdict.TooFast or ClickVerdict.TooEven when verdict != _lastRejection:
                // Каждый отклонённый клик в журнал не пишется: автокликер заполнил бы его за минуты.
                context.Log.Warn($"Клики не засчитываются: {verdict}");
                _lastRejection = verdict;
                break;
        }
    }
}
