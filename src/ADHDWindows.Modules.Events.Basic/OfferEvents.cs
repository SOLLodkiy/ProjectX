using System.Globalization;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.RandomEvents;

namespace ADHDWindows.Modules.Events.Basic;

/// <summary>Настройки «Часа удачи»: множитель всего дохода и длительность в минутах.</summary>
public sealed record LuckyHourSettings(decimal Multiplier = 2, double Minutes = 5);

/// <summary>Час удачи: весь доход ×2 на 5 минут. Ничего не рисует и уживается с усложнениями.</summary>
public sealed class LuckyHourEvent : RandomEventModule
{
    private LuckyHourSettings _settings = new();

    public override RandomEventInfo Info { get; } = new("luckyHour", HardshipResources.None);

    protected override void OnStart()
    {
        _settings = EventData.Load(Context.Data, "events/rewards", "luckyHour", new LuckyHourSettings(), Context.Log);
        if (_settings.Multiplier <= 1 || _settings.Minutes <= 0)
        {
            Context.Log.Error("Неверный «Час удачи», действует значение по умолчанию");
            _settings = new LuckyHourSettings();
        }
    }

    protected override bool Begin(string? variant)
    {
        Context.Events.Publish(new BoosterGrantRequested(Info.Id, _settings.Multiplier, TimeSpan.FromMinutes(_settings.Minutes)));
        Context.Events.Publish(new RandomEventNotice(
            "clock",
            Context.Texts.Get("events.luckyHour.title"),
            Context.Texts.Get(
                "events.luckyHour.notice",
                _settings.Multiplier.ToString("0.##", CultureInfo.CurrentCulture),
                _settings.Minutes.ToString("0", CultureInfo.CurrentCulture))));
        Complete(true, 0);
        return true;
    }

    protected override void End()
    {
    }
}

/// <summary>Настройки распродажи: скидка от и до в процентах и сколько часов она держится.</summary>
public sealed record SaleSettings(int MinPercent = 30, int MaxPercent = 60, double Hours = 1);

/// <summary>
/// Распродажа: скидка на случайный сайт или программу из каталога, которые ещё не куплены. Скидку держит модуль
/// блокировки; если уценить нечего или блокировки нет, событие недоступно и планировщик выбирает другое.
/// </summary>
public sealed class SaleEvent : RandomEventModule
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);

    private SaleSettings _settings = new();

    public override RandomEventInfo Info { get; } = new("sale", HardshipResources.None);

    protected override void OnStart() =>
        _settings = EventData.Load(Context.Data, "events/rewards", "sale", new SaleSettings(), Context.Log);

    protected override bool Begin(string? variant)
    {
        var requester = $"{Info.Id}.{Guid.NewGuid():N}";
        LockSaleStarted? started = null;
        using var replied = new ManualResetEventSlim();
        using var onStarted = Context.Events.Subscribe<LockSaleStarted>(sale =>
        {
            if (sale.Requester == requester)
            {
                started = sale;
                replied.Set();
            }
        });
        using var onUnavailable = Context.Events.Subscribe<LockSaleUnavailable>(sale =>
        {
            if (sale.Requester == requester)
            {
                replied.Set();
            }
        });

        var min = Math.Clamp(_settings.MinPercent, 1, 90);
        var max = Math.Clamp(_settings.MaxPercent, min, 90);
        var percent = Random.Shared.Next(min, max + 1);
        Context.Events.Publish(new LockSaleRequested(requester, percent, TimeSpan.FromHours(Math.Max(0.1, _settings.Hours))));

        // Модуль блокировки отвечает из своего потока; без него ответа нет.
        if (!replied.Wait(ReplyTimeout) || started is null)
        {
            return false;
        }

        Complete(true, 0);
        return true;
    }

    protected override void End()
    {
    }
}

/// <summary>
/// Сектор колеса. <paramref name="Kind"/>: <c>coins</c> (<paramref name="Amount"/> монет), <c>booster</c> (весь доход
/// ×<paramref name="Multiplier"/> на <paramref name="Minutes"/>), <c>chest</c> (сундук <paramref name="Tier"/>),
/// <c>nothing</c>.
/// </summary>
public sealed record WheelSegment(string Kind, double Weight, long Amount = 0, decimal Multiplier = 1, double Minutes = 0, string? Tier = null);

/// <summary>Колесо фортуны: цена дополнительного спина и секторы.</summary>
public sealed record WheelSettings(long Price, IReadOnlyList<WheelSegment> Segments)
{
    public static WheelSettings Default { get; } = new(100, [new WheelSegment("coins", 1, 50), new WheelSegment("coins", 1, 100), new WheelSegment("nothing", 1)]);
}

/// <summary>
/// Колесо фортуны: один бесплатный спин в день, дополнительные — за монетки. Открывается из меню трея в любое время,
/// а случайно появляется, только пока бесплатный спин не использован.
/// </summary>
public sealed class WheelEvent : DialogEvent
{
    public const string FreeDaySave = "freeDay";

    private readonly Lock _lock = new();
    private WheelSettings _settings = WheelSettings.Default;
    private int _spins;
    private long _earned;
    private string _lastText = "";

    public override RandomEventInfo Info { get; } = new("wheel", HardshipResources.Screen, Manual: true);

    /// <summary>Сегодняшняя дата, для проверок подменяется.</summary>
    internal Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    internal bool FreeSpinAvailable => Context.Saves.Get<DateOnly?>(FreeDaySave) != Today();

    internal IReadOnlyList<WheelSegment> Segments => _settings.Segments;

    protected override void OnStart()
    {
        var loaded = EventData.Load(Context.Data, "events/wheel", null, WheelSettings.Default, Context.Log);
        var segments = (loaded.Segments ?? []).Where(Valid).ToList();
        if (segments.Count < 2 || loaded.Price <= 0)
        {
            Context.Log.Error("Неверное колесо фортуны, действует значение по умолчанию");
            _settings = WheelSettings.Default;
            return;
        }

        _settings = loaded with { Segments = segments };
    }

    protected override EventDialog? Open(string? variant)
    {
        lock (_lock)
        {
            if (variant != RandomEventVariants.Manual && !FreeSpinAvailable)
            {
                return null;
            }

            _spins = 0;
            _earned = 0;
            _lastText = Text("events.wheel.text");
            return Build(null);
        }
    }

    protected override void OnChoice(string choice)
    {
        lock (_lock)
        {
            if (choice == "spin")
            {
                Spin();
                return;
            }
        }

        if (choice == EventDialogChosen.CloseChoice)
        {
            Complete(_spins > 0, _earned);
        }
    }

    private void Spin()
    {
        if (FreeSpinAvailable)
        {
            Context.Saves.Set(FreeDaySave, Today());
        }
        else if (!Context.Economy.TrySpend(_settings.Price, "event:wheel"))
        {
            _lastText = Text("events.wheel.poor", CoinFormat.Short(_settings.Price));
            Show(Build(null));
            return;
        }

        var index = Roll();
        var segment = _settings.Segments[index];
        _spins++;
        switch (segment.Kind)
        {
            case "coins":
                var coins = Context.Economy.Earn(EventSources.Event, segment.Amount);
                _earned += coins;
                _lastText = Text("events.wheel.won.coins", CoinFormat.Short(coins));
                break;
            case "booster":
                Context.Events.Publish(new BoosterGrantRequested(Info.Id, segment.Multiplier, TimeSpan.FromMinutes(segment.Minutes)));
                _lastText = Text("events.wheel.won.booster", Label(segment), segment.Minutes.ToString("0", CultureInfo.CurrentCulture));
                break;
            case "chest":
                Context.Events.Publish(new ChestGrantRequested(segment.Tier ?? ChestTiers.Common, "wheel"));
                _lastText = Text("events.wheel.won.chest", Label(segment));
                break;
            default:
                _lastText = Text("events.wheel.won.nothing");
                break;
        }

        Context.Log.Info($"Колесо: {segment.Kind} {segment.Amount}");
        Show(Build(index));
    }

    private EventDialog Build(int? result)
    {
        var free = FreeSpinAvailable;
        var spin = free
            ? new EventDialogChoice("spin", Text("events.wheel.free"))
            : new EventDialogChoice("spin", Text("events.wheel.paid"), _settings.Price);
        return Dialog(Text("events.wheel.title"), _lastText, [spin], "star") with
        {
            Wheel = [.. _settings.Segments.Select(Label)],
            WheelResult = result,
            Spin = _spins,
            Expires = null,
        };
    }

    private int Roll()
    {
        var roll = Random.NextDouble() * _settings.Segments.Sum(segment => segment.Weight);
        for (var index = 0; index < _settings.Segments.Count; index++)
        {
            roll -= _settings.Segments[index].Weight;
            if (roll < 0)
            {
                return index;
            }
        }

        return _settings.Segments.Count - 1;
    }

    private string Label(WheelSegment segment) => segment.Kind switch
    {
        "coins" => CoinFormat.Short(segment.Amount),
        "booster" => "×" + segment.Multiplier.ToString("0.##", CultureInfo.CurrentCulture),
        "chest" => Text($"events.wheel.chest.{segment.Tier ?? ChestTiers.Common}"),
        _ => Text("events.wheel.nothing"),
    };

    private static bool Valid(WheelSegment segment) => segment.Weight > 0 && segment.Kind switch
    {
        "coins" => segment.Amount > 0,
        "booster" => segment.Multiplier > 1 && segment.Minutes > 0,
        "chest" => segment.Tier is null || ChestTiers.All.Contains(segment.Tier),
        "nothing" => true,
        _ => false,
    };
}

/// <summary>Загадка: вопрос, варианты ответа, номер верного (с нуля) и награда.</summary>
public sealed record Riddle(string Id, string Question, IReadOnlyList<string> Answers, int Correct, long Reward);

public sealed record RiddleList(IReadOnlyList<Riddle> Riddles);

/// <summary>
/// Загадка: простая мини-викторина. Верный ответ приносит монетки, неверный — подсказку с правильным ответом.
/// Последние загадки не повторяются.
/// </summary>
public sealed class RiddleEvent : DialogEvent
{
    public const string RecentSave = "recent";
    private const int RecentLimit = 10;

    private readonly Lock _lock = new();
    private IReadOnlyList<Riddle> _riddles = [];
    private Riddle? _riddle;
    private List<int> _order = [];
    private bool _answered;

    public override RandomEventInfo Info { get; } = new("riddle", HardshipResources.Screen);

    internal Riddle? Current => _riddle;

    /// <summary>Порядок ответов на кнопках: номер ответа из данных для каждой кнопки.</summary>
    internal IReadOnlyList<int> Order => _order;

    protected override void OnStart()
    {
        var loaded = EventData.Load(Context.Data, "events/riddles", null, new RiddleList([]), Context.Log);
        _riddles = [.. (loaded.Riddles ?? []).Where(riddle =>
        {
            var valid = !string.IsNullOrEmpty(riddle.Id) && riddle.Answers is { Count: >= 2 } && riddle.Correct >= 0
                && riddle.Correct < riddle.Answers.Count && riddle.Reward >= 0 && !string.IsNullOrWhiteSpace(riddle.Question);
            if (!valid)
            {
                Context.Log.Error($"Неверная загадка пропущена: {riddle.Id}");
            }

            return valid;
        })];
    }

    protected override EventDialog? Open(string? variant)
    {
        lock (_lock)
        {
            var recent = Context.Saves.Get<List<string>>(RecentSave) ?? [];
            var pool = _riddles.Where(riddle => !recent.Contains(riddle.Id)).ToList();
            if (pool.Count == 0)
            {
                pool = [.. _riddles];
            }

            if (pool.Count == 0)
            {
                return null;
            }

            _riddle = variant is not null && _riddles.FirstOrDefault(riddle => riddle.Id == variant) is { } chosen
                ? chosen
                : pool[Random.Next(pool.Count)];
            recent.Remove(_riddle.Id);
            recent.Add(_riddle.Id);
            Context.Saves.Set(RecentSave, recent.TakeLast(RecentLimit).ToList());
            _answered = false;
            _order = [.. Enumerable.Range(0, _riddle.Answers.Count).OrderBy(_ => Random.Next())];
            var choices = _order.Select((answer, button) => new EventDialogChoice($"answer.{button}", _riddle.Answers[answer])).ToList();
            return Dialog(Text("events.riddle.title"), Text("events.riddle.text", _riddle.Question, CoinFormat.Short(_riddle.Reward)), choices, "quest");
        }
    }

    protected override void OnChoice(string choice)
    {
        long earned;
        bool correct;
        Riddle riddle;
        lock (_lock)
        {
            if (_answered || _riddle is null || !choice.StartsWith("answer.", StringComparison.Ordinal)
                || !int.TryParse(choice["answer.".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var button)
                || button >= _order.Count)
            {
                if (choice == EventDialogChosen.CloseChoice && !_answered)
                {
                    _answered = true;
                    Complete(false, 0);
                }

                return;
            }

            _answered = true;
            riddle = _riddle;
            correct = _order[button] == riddle.Correct;
            earned = correct ? Context.Economy.Earn(EventSources.Event, riddle.Reward) : 0;
        }

        Context.Events.Publish(correct
            ? new RandomEventNotice("check", Text("events.riddle.correct"), Text("events.riddle.correct.text", CoinFormat.Short(earned)))
            : new RandomEventNotice("close", Text("events.riddle.wrong"), Text("events.riddle.wrong.text", riddle.Answers[riddle.Correct]), Reward: false));
        Complete(correct, earned);
    }
}

/// <summary>
/// Товар торговца. <paramref name="Kind"/>: <c>booster</c> (доход <paramref name="Source"/>, null — весь, ×<paramref name="Multiplier"/>
/// на <paramref name="Minutes"/>) или <c>chest</c> (сундук <paramref name="Tier"/>).
/// </summary>
public sealed record MerchantOffer(string Id, string Kind, long Price, decimal Multiplier = 1, double Minutes = 0, string? Source = null, string? Tier = null);

public sealed record MerchantSettings(IReadOnlyList<MerchantOffer> Offers, int Count = 3);

/// <summary>Торговец: на короткое время появляется редкий продавец с тремя случайными товарами.</summary>
public sealed class MerchantEvent : DialogEvent
{
    private readonly Lock _lock = new();
    private MerchantSettings _settings = new([]);
    private List<MerchantOffer> _stock = [];
    private readonly HashSet<string> _bought = new(StringComparer.Ordinal);
    private string _text = "";

    public override RandomEventInfo Info { get; } = new("merchant", HardshipResources.Screen);

    internal IReadOnlyList<MerchantOffer> Stock => _stock;

    protected override void OnStart()
    {
        var loaded = EventData.Load(Context.Data, "events/merchant", null, new MerchantSettings([]), Context.Log);
        var offers = (loaded.Offers ?? []).Where(offer =>
        {
            var valid = !string.IsNullOrEmpty(offer.Id) && offer.Price > 0 && offer.Kind switch
            {
                "booster" => offer.Multiplier > 1 && offer.Minutes > 0,
                "chest" => offer.Tier is not null && ChestTiers.All.Contains(offer.Tier),
                _ => false,
            };
            if (!valid)
            {
                Context.Log.Error($"Неверный товар торговца пропущен: {offer.Id}");
            }

            return valid;
        }).ToList();
        _settings = loaded with { Offers = offers, Count = Math.Max(1, loaded.Count) };
    }

    protected override EventDialog? Open(string? variant)
    {
        lock (_lock)
        {
            if (_settings.Offers.Count == 0)
            {
                return null;
            }

            _bought.Clear();
            _stock = [.. _settings.Offers.OrderBy(_ => Random.Next()).Take(_settings.Count)];
            _text = Text("events.merchant.text");
            return Build();
        }
    }

    protected override void OnChoice(string choice)
    {
        if (choice == EventDialogChosen.CloseChoice)
        {
            bool any;
            lock (_lock)
            {
                any = _bought.Count > 0;
            }

            Complete(any, 0);
            return;
        }

        lock (_lock)
        {
            if (_stock.FirstOrDefault(offer => offer.Id == choice) is not { } offer || _bought.Contains(offer.Id))
            {
                return;
            }

            if (!Context.Economy.TrySpend(offer.Price, $"event:merchant:{offer.Id}"))
            {
                _text = Text("events.merchant.poor");
                Show(Build());
                return;
            }

            _bought.Add(offer.Id);
            if (offer.Kind == "booster")
            {
                Context.Events.Publish(new BoosterGrantRequested($"merchant.{offer.Source ?? "all"}", offer.Multiplier, TimeSpan.FromMinutes(offer.Minutes), offer.Source));
            }
            else
            {
                Context.Events.Publish(new ChestGrantRequested(offer.Tier!, "merchant"));
            }

            Context.Log.Info($"Куплено у торговца: {offer.Id} за {offer.Price}");
            _text = Text("events.merchant.thanks");
            Show(Build());
        }
    }

    private EventDialog Build() => Dialog(
        Text("events.merchant.title"),
        _text,
        [.. _stock.Select(offer => new EventDialogChoice(offer.Id, Describe(offer), offer.Price, !_bought.Contains(offer.Id)))],
        "shop");

    private string Describe(MerchantOffer offer) => offer.Kind == "booster"
        ? Text(
            "events.merchant.booster",
            Text($"events.source.{offer.Source ?? "all"}"),
            offer.Multiplier.ToString("0.##", CultureInfo.CurrentCulture),
            offer.Minutes.ToString("0", CultureInfo.CurrentCulture))
        : Text("events.merchant.chest", Text($"events.chest.tier.{offer.Tier}"));
}
