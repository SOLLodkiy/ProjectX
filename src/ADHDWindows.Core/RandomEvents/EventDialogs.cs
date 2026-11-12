namespace ADHDWindows.Core.RandomEvents;

/// <summary>
/// Кнопка окна события. <paramref name="Price"/> больше нуля — кнопка показывает цену и недоступна,
/// если монеток не хватает.
/// </summary>
public sealed record EventDialogChoice(string Id, string Text, long Price = 0, bool Enabled = true);

/// <summary>
/// Окно события: загадка, торговец, колесо фортуны, содержимое сундука. Приложение рисует его по этому описанию,
/// поэтому новое событие с окном не требует правки приложения. <paramref name="Wheel"/> — подписи секторов колеса;
/// когда приходит <paramref name="WheelResult"/>, колесо крутится и останавливается на этом секторе,
/// и только после остановки показывается <paramref name="Text"/>. Повторная публикация с тем же
/// <paramref name="Id"/> обновляет окно.
/// </summary>
public sealed record EventDialog(
    string Id,
    string Icon,
    string Title,
    string Text,
    IReadOnlyList<EventDialogChoice> Choices,
    DateTimeOffset? Expires = null,
    IReadOnlyList<string>? Wheel = null,
    int? WheelResult = null,
    int Spin = 0);

/// <summary>Показать или обновить окно события.</summary>
public sealed record EventDialogShown(EventDialog Dialog);

/// <summary>Закрыть окно события.</summary>
public sealed record EventDialogClosed(string Id);

/// <summary>Игрок нажал кнопку окна. <see cref="CloseChoice"/> — закрыл окно крестиком.</summary>
public sealed record EventDialogChosen(string DialogId, string ChoiceId)
{
    public const string CloseChoice = "close";
}

/// <summary>Временный множитель дохода, например «Час удачи» или бустер из сундука.</summary>
public sealed record BoosterView(string Id, decimal Multiplier, string? Source, DateTimeOffset Ends);

/// <summary>
/// Выдать бустер: доход источника <paramref name="Source"/> (null — весь доход) умножается на
/// <paramref name="Multiplier"/> на <paramref name="Duration"/>. Бустер с тем же <paramref name="Id"/> продлевается.
/// </summary>
public sealed record BoosterGrantRequested(string Id, decimal Multiplier, TimeSpan Duration, string? Source = null);

/// <summary>Действующие бустеры. Приходит при изменениях и по запросу <see cref="BoostersRequested"/>.</summary>
public sealed record BoostersChanged(IReadOnlyList<BoosterView> Boosters);

public sealed record BoostersRequested;

/// <summary>Редкость сундука.</summary>
public static class ChestTiers
{
    public const string Common = "common";
    public const string Rare = "rare";
    public const string Legendary = "legendary";

    public static IReadOnlyList<string> All { get; } = [Common, Rare, Legendary];
}

/// <summary>Открыть сундук сразу, без появления на экране: покупка у торговца, приз колеса.</summary>
public sealed record ChestGrantRequested(string Tier, string Reason);

/// <summary>Что лежало в сундуке. <paramref name="Kind"/>: coins, booster, cosmetic.</summary>
public sealed record ChestItem(string Kind, string Text, long Coins = 0, string? Code = null);

/// <summary>Сундук открыт, содержимое уже выдано.</summary>
public sealed record ChestOpened(string Tier, IReadOnlyList<ChestItem> Items, string Reason);

/// <summary>Уведомление от события: итог загадки, выигрыш колеса, начало «Часа удачи».</summary>
public sealed record RandomEventNotice(string Icon, string Title, string Text, bool Reward = true);
