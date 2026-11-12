using System.Globalization;
using ADHDWindows.Core.Cosmetics;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.RandomEvents;
using ADHDWindows.Core.Stats;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Events.Basic;

/// <summary>
/// Картинка появляется в случайном месте монитора с курсором и ждёт клика: успел — награда, нет — исчезает.
/// Клик забирает окно картинки и не попадает в окно под ней.
/// </summary>
public abstract class ClickTargetEvent : OverlayEvent
{
    private SpriteOverlay? _sprite;
    private Picture? _shown;
    private ScreenPoint _home;
    private double _age;
    private bool _claimed;

    /// <summary>Картинка уже поймана, для проверок.</summary>
    internal bool Claimed => _claimed;

    /// <summary>Где картинка на экране, для проверок. Null — её нет.</summary>
    internal ScreenRect? TargetBounds => Overlay.Invoke(() => _sprite is { IsVisible: true } sprite ? sprite.Bounds : (ScreenRect?)null);

    /// <summary>Сколько секунд картинка ждёт клика.</summary>
    protected abstract double Seconds { get; }

    protected abstract int Scale { get; }

    /// <summary>Кадр анимации в момент <paramref name="age"/>.</summary>
    protected abstract Picture Frame(double age);

    /// <summary>Выдаёт награду за клик и возвращает зачисленные монетки.</summary>
    protected abstract long Claim();

    /// <summary>Клик по картинке, для проверок: как клик мышью.</summary>
    internal void ClickForTest() => Overlay.Invoke(() => OnClick(1, 1));

    protected override void Create()
    {
        _claimed = false;
        _age = 0;
        _shown = null;
        var first = Frame(0);
        _home = EventArea.RandomSpot(Random, EventArea.WorkArea(), first.Width * Scale, first.Height * Scale);
        _sprite = Overlay.CreateSprite(OnClick);
        Draw(_sprite, first, Scale, _home, ref _shown);
    }

    protected override void Tick(double seconds)
    {
        if (_sprite is null || _claimed)
        {
            return;
        }

        _age += seconds;
        if (_age >= Seconds)
        {
            _sprite.Hide();
            Complete(false, 0);
            return;
        }

        // Лёгкое покачивание вверх-вниз, чтобы картинку было видно краем глаза.
        var bob = (int)Math.Round(Math.Sin(_age * 4) * Scale);
        Draw(_sprite, Frame(_age), Scale, _home with { Y = _home.Y + bob }, ref _shown);
    }

    protected override void Finish() => _sprite = null;

    private void OnClick(int x, int y)
    {
        if (_claimed || _sprite is null || _age >= Seconds)
        {
            return;
        }

        _claimed = true;
        _sprite.Hide();
        Complete(true, Claim());
    }
}

/// <summary>Настройки золотой монеты: сколько секунд видна и сколько даёт клик.</summary>
public sealed record GoldenCoinSettings(double Seconds = 8, long Reward = 150, int Scale = 5);

/// <summary>Золотая монета: появляется на 8 секунд, клик даёт крупную сумму.</summary>
public sealed class GoldenCoinEvent : ClickTargetEvent
{
    private GoldenCoinSettings _settings = new();

    public override RandomEventInfo Info { get; } = new("goldenCoin", HardshipResources.Screen);

    protected override double Seconds => _settings.Seconds;

    protected override int Scale => _settings.Scale;

    protected override void OnStart() =>
        _settings = EventData.Load(Context.Data, "events/rewards", "goldenCoin", new GoldenCoinSettings(), Context.Log);

    protected override Picture Frame(double age) => age % 0.8 < 0.6 ? EventPictures.GoldenCoin : EventPictures.GoldenCoinEdge;

    protected override long Claim() => Context.Economy.Earn(EventSources.Event, _settings.Reward);
}

/// <summary>Сезон: даты «месяц-день» включительно, может переходить через Новый год.</summary>
public sealed record Season(string Id, string From, string To, long Reward, string Picture, string? Cosmetic = null)
{
    public bool Contains(DateOnly date)
    {
        if (!TryParse(From, out var from) || !TryParse(To, out var to))
        {
            return false;
        }

        var today = (date.Month * 100) + date.Day;
        return from <= to ? today >= from && today <= to : today >= from || today <= to;
    }

    public bool IsValid => TryParse(From, out _) && TryParse(To, out _) && Reward >= 0 && !string.IsNullOrEmpty(Id);

    private static bool TryParse(string text, out int value)
    {
        value = 0;
        if (DateOnly.TryParseExact($"2024-{text}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            value = (date.Month * 100) + date.Day;
            return true;
        }

        return false;
    }
}

public sealed record SeasonList(IReadOnlyList<Season> Seasons, double Seconds = 10, int Scale = 5);

/// <summary>
/// Сезонное событие: в Новый год, Хэллоуин и другие праздники появляется праздничная картинка. Клик даёт особую
/// награду и косметику сезона, если модуль косметики её предлагает. Вне сезонов событие недоступно.
/// </summary>
public sealed class SeasonalEvent : ClickTargetEvent
{
    private SeasonList _seasons = new([]);
    private Season? _season;
    private IReadOnlyList<string> _cosmetics = [];

    public override RandomEventInfo Info { get; } = new("seasonal", HardshipResources.Screen);

    /// <summary>Сегодняшняя дата, для проверок подменяется.</summary>
    internal Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Сезон, который сейчас идёт, или null.</summary>
    internal Season? Current => _seasons.Seasons.FirstOrDefault(season => season.Contains(Today()));

    protected override double Seconds => _seasons.Seconds;

    protected override int Scale => _seasons.Scale;

    protected override void OnStart()
    {
        _seasons = EventData.Load(Context.Data, "events/seasons", null, new SeasonList([]), Context.Log);
        foreach (var broken in _seasons.Seasons.Where(season => !season.IsValid))
        {
            Context.Log.Error($"Неверный сезон пропущен: {broken.Id}");
        }

        _seasons = _seasons with { Seasons = [.. _seasons.Seasons.Where(season => season.IsValid)] };
        Context.Events.Subscribe<CosmeticsAvailable>(available => _cosmetics = available.Codes);
    }

    protected override bool Prepare(string? variant)
    {
        _season = variant is null ? Current : _seasons.Seasons.FirstOrDefault(season => season.Id == variant);
        if (_season is null)
        {
            return false;
        }

        Context.Events.Publish(new CosmeticsAvailableRequested());
        return true;
    }

    protected override Picture Frame(double age) => _season?.Picture switch
    {
        "pumpkin" => EventPictures.Pumpkin,
        "heart" => EventPictures.Heart,
        "flower" => EventPictures.Flower,
        "snowflake" => EventPictures.Snowflake,
        _ => EventPictures.Gift,
    };

    protected override long Claim()
    {
        var season = _season!;
        var earned = Context.Economy.Earn(EventSources.Event, season.Reward);
        if (season.Cosmetic is { } cosmetic && _cosmetics.Contains(cosmetic))
        {
            Context.Events.Publish(new CosmeticGrantRequested(cosmetic, $"season.{season.Id}"));
        }

        Context.Events.Publish(new StatCounted($"events.season.{season.Id}"));
        return earned;
    }
}
