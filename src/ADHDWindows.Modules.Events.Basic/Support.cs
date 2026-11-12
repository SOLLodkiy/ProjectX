using System.Text.Json;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.RandomEvents;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Events.Basic;

/// <summary>Источники дохода событий: награды событий и содержимое сундуков.</summary>
public static class EventSources
{
    public const string Event = "event";
    public const string Chest = "chest";
}

/// <summary>Настройки событий из data/events.</summary>
public static class EventData
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Раздел <paramref name="section"/> файла data/<paramref name="key"/>.json или весь файл, если раздел null.
    /// Нет данных или они неверны — значения по умолчанию из <paramref name="fallback"/>, с записью в журнал.
    /// </summary>
    public static T Load<T>(GameData data, string key, string? section, T fallback, ILog log)
        where T : class
    {
        if (!data.TryGet<JsonElement>(key, out var file))
        {
            log.Warn($"Нет данных событий {key}, действуют значения по умолчанию");
            return fallback;
        }

        if (section is not null && (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty(section, out file)))
        {
            log.Warn($"Нет раздела {section} в данных {key}, действуют значения по умолчанию");
            return fallback;
        }

        try
        {
            return file.Deserialize<T>(Options) ?? fallback;
        }
        catch (JsonException exception)
        {
            log.Error($"Данные событий {key}/{section} имеют неверный формат: {exception.Message}", exception);
            return fallback;
        }
    }

    /// <summary>Случайное целое от <paramref name="min"/> до <paramref name="max"/> включительно.</summary>
    public static long Between(Random random, long min, long max) =>
        max <= min ? min : min + random.NextInt64(max - min + 1);
}

/// <summary>
/// Событие поверх экрана в своём потоке окон, как экранные усложнения. Клик по картинке события забирает окно
/// оверлея, поэтому он не попадает в окно под ним. Окна исчезают вместе с программой — откатывать нечего.
/// </summary>
public abstract class OverlayEvent : RandomEventModule
{
    private const int LoggedErrors = 3;

    private OverlayThread? _overlay;
    private int _errors;

    /// <summary>Сколько окон оверлея сейчас открыто, для проверок.</summary>
    internal int OverlayWindows => _overlay?.WindowCount ?? 0;

    protected OverlayThread Overlay => _overlay ?? throw new InvalidOperationException("Оверлей не запущен");

    protected Random Random { get; } = new();

    protected override bool Begin(string? variant)
    {
        if (!Prepare(variant))
        {
            return false;
        }

        _errors = 0;
        _overlay = new OverlayThread("event." + Info.Id, Tick, OnError);
        _overlay.Invoke(Create);
        return true;
    }

    protected override void End()
    {
        _overlay?.Dispose();
        _overlay = null;
        Finish();
    }

    /// <summary>Проверка до появления окон: false — событию сейчас нечего показать.</summary>
    protected virtual bool Prepare(string? variant) => true;

    /// <summary>Создаёт окна в потоке оверлея.</summary>
    protected abstract void Create();

    /// <summary>Шаг анимации в потоке оверлея, примерно 60 раз в секунду.</summary>
    protected abstract void Tick(double seconds);

    protected virtual void Finish()
    {
    }

    /// <summary>Сколько секунд осталось до конца, который назначил планировщик.</summary>
    protected double SecondsLeft => (Ends - DateTimeOffset.UtcNow).TotalSeconds;

    /// <summary>Показывает картинку или только двигает её, если она уже показана.</summary>
    protected static void Draw(SpriteOverlay sprite, Picture picture, int scale, ScreenPoint position, ref Picture? shown)
    {
        if (!ReferenceEquals(picture, shown))
        {
            shown = picture;
            sprite.Show(picture.Pixels, picture.Width, picture.Height, scale, position);
        }
        else
        {
            sprite.MoveTo(position);
        }
    }

    private void OnError(Exception exception)
    {
        if (Interlocked.Increment(ref _errors) <= LoggedErrors)
        {
            Context.Log.Error($"Ошибка события {Info.Id}", exception);
        }
    }
}

/// <summary>
/// Событие в окне программы: загадка, торговец, колесо. Окно рисует приложение по <see cref="EventDialog"/>,
/// а нажатые кнопки приходят в <see cref="OnChoice"/> (в потоке интерфейса — долгих действий там не нужно).
/// </summary>
public abstract class DialogEvent : RandomEventModule
{
    private IDisposable? _subscription;
    private string? _dialogId;

    protected Random Random { get; } = new();

    /// <summary>Код открытого окна, для проверок.</summary>
    internal string? DialogId => _dialogId;

    protected override bool Begin(string? variant)
    {
        _dialogId = $"{Info.Id}.{Guid.NewGuid():N}";
        var id = _dialogId;
        _subscription = Context.Events.Subscribe<EventDialogChosen>(chosen =>
        {
            if (chosen.DialogId == id)
            {
                OnChoice(chosen.ChoiceId);
            }
        });

        if (Open(variant) is { } dialog)
        {
            Show(dialog);
            return true;
        }

        _subscription.Dispose();
        _subscription = null;
        _dialogId = null;
        return false;
    }

    protected override void End()
    {
        _subscription?.Dispose();
        _subscription = null;
        if (_dialogId is { } id)
        {
            Context.Events.Publish(new EventDialogClosed(id));
        }

        _dialogId = null;
        Finish();
    }

    /// <summary>Первое содержимое окна или null, если событию сейчас нечего показать.</summary>
    protected abstract EventDialog? Open(string? variant);

    /// <summary>Игрок нажал кнопку. <see cref="EventDialogChosen.CloseChoice"/> — закрыл окно.</summary>
    protected abstract void OnChoice(string choice);

    protected virtual void Finish()
    {
    }

    /// <summary>Окно с кодом этого события: заполняет код, значок и срок.</summary>
    protected EventDialog Dialog(string title, string text, IReadOnlyList<EventDialogChoice> choices, string icon = "star") =>
        new(_dialogId ?? "", icon, title, text, choices, Ends);

    protected void Show(EventDialog dialog) => Context.Events.Publish(new EventDialogShown(dialog with { Id = _dialogId ?? dialog.Id }));

    protected string Text(string key, params object[] args) => Context.Texts.Get(key, args);
}

/// <summary>Куда ставить картинки событий: рабочая область монитора с курсором или тестового монитора.</summary>
internal static class EventArea
{
    public static ScreenRect WorkArea() => OverlayArea.WorkArea();

    /// <summary>Случайная точка, где картинка размером <paramref name="width"/>×<paramref name="height"/> видна целиком.</summary>
    public static ScreenPoint RandomSpot(Random random, ScreenRect area, int width, int height, int margin = 40)
    {
        var left = area.Left + margin;
        var top = area.Top + margin;
        var spanX = Math.Max(1, area.Width - width - (2 * margin));
        var spanY = Math.Max(1, area.Height - height - (2 * margin));
        return new ScreenPoint(left + random.Next(spanX), top + random.Next(spanY));
    }
}
