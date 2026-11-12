using System.Collections.Concurrent;
using ADHDWindows.Core.Cosmetics;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Progress;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.RandomEvents;
using ADHDWindows.Core.Upgrades;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Pet;

/// <summary>
/// Пиксельный питомец рядом с HUD или над панелью задач. Радуется заданиям, покупкам, уровням и пережитым усложнениям,
/// ест, когда его кормят, и спит, пока человек ничего не делает. Его можно кормить монетками: с порциями он растёт.
/// Вид питомца — косметика места Pet, которую надевает модуль косметики. Питомец прячется в полноэкранных окнах,
/// на защищённом экране и пока на экране идёт усложнение или событие. Выключается в настройках.
/// </summary>
public sealed class PetModule : IModule
{
    public const string EnabledSetting = "enabled";
    public const string PlaceSetting = "place";
    public const string FoodKey = "food";
    public const string SpendReason = "pet:feed";

    /// <summary>Режим разработчика: питомец не прячется в полноэкранных окнах. Нужен проверкам на компьютере с открытым видео.</summary>
    public const string IgnoreSafetySetting = "test/ignoreSafety";

    private const int TickMs = 50;
    private const double CheckSeconds = 1;
    private const int HudGap = 4;
    private const int TaskbarMargin = 260;
    private const double WanderSpeed = 12;

    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan> _idle;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, HardshipInfo> _hardships = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _screenBusy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _food = new(StringComparer.Ordinal);
    private readonly Random _random = new();
    private IModuleContext? _context;
    private PetRules _rules = PetRules.Default;
    private IReadOnlyDictionary<char, uint> _colors = Colors.Keys;
    private CosmeticItem? _species;
    private Dictionary<string, IReadOnlyList<Picture>> _frames = [];
    private bool _enabled;
    private bool _ignoreSafety;
    private PetPlace _place;
    private PetMood _mood;
    private DateTimeOffset _moodUntil;
    private ScreenRect? _hud;
    private OverlayThread? _overlay;
    private SpriteOverlay? _sprite;
    private Picture? _shown;
    private int _shownScale;
    private double _sinceCheck = CheckSeconds;
    private double _age;
    private string? _hiddenBecause;
    private double _wander;
    private double _wanderTarget;
    private int _errors;

    public PetModule()
        : this(TimeProvider.System, new InputActivity().Idle)
    {
    }

    internal PetModule(TimeProvider clock, Func<TimeSpan> idle)
    {
        _clock = clock;
        _idle = idle;
    }

    public string Id => "pet";

    public string Name => "Питомец";

    /// <summary>Питомец сейчас виден на экране, для проверок.</summary>
    internal bool Visible
    {
        get
        {
            var overlay = Overlay;
            return overlay is not null && overlay.Invoke(() => _sprite?.IsVisible == true);
        }
    }

    /// <summary>Где питомец на экране, для проверок.</summary>
    internal ScreenRect? Bounds
    {
        get
        {
            var overlay = Overlay;
            return overlay?.Invoke(() => _sprite is { IsVisible: true } sprite ? sprite.Bounds : (ScreenRect?)null);
        }
    }

    /// <summary>Почему питомец спрятан, для проверок: null — не спрятан.</summary>
    internal string? HiddenBecause
    {
        get
        {
            lock (_lock)
            {
                return _hiddenBecause;
            }
        }
    }

    internal PetMood Mood
    {
        get
        {
            lock (_lock)
            {
                return _mood;
            }
        }
    }

    internal bool Running => Overlay is not null;

    private OverlayThread? Overlay
    {
        get
        {
            lock (_lock)
            {
                return _overlay;
            }
        }
    }

    public void Start(IModuleContext context)
    {
        _context = context;
        _rules = PetRules.Load(context.Data, context.Log);
        _colors = LoadColors(context);
        _enabled = context.Settings.Get(EnabledSetting, true);
        _ignoreSafety = context.DeveloperMode && context.Settings.Get(IgnoreSafetySetting, false);
        _place = Enum.TryParse<PetPlace>(context.Settings.Get<string>(PlaceSetting), ignoreCase: true, out var place) ? place : PetPlace.Hud;
        foreach (var (species, food) in context.Saves.Get<Dictionary<string, long>>(FoodKey) ?? [])
        {
            _food[species] = Math.Max(0, food);
        }

        var events = context.Events;
        events.Subscribe<CosmeticsChanged>(change => SetSpecies(change.Equipped.GetValueOrDefault(CosmeticSlot.Pet)));
        events.Subscribe<PetRequested>(_ => Publish());
        events.Subscribe<PetFeedRequested>(_ => Feed());
        events.Subscribe<PetSettingsRequested>(request => Configure(request.Enabled, request.Place));
        events.Subscribe<HudMoved>(moved =>
        {
            lock (_lock)
            {
                _hud = moved.Visible ? moved.Bar : null;
            }
        });

        // Экран занят усложнением или событием: питомец не мешает им и не прячет их картинки.
        events.Subscribe<HardshipOffered>(offer => _hardships[offer.Hardship.Id] = offer.Hardship);
        events.Subscribe<HardshipStartRequested>(request =>
        {
            if (_hardships.TryGetValue(request.Id, out var info) && info.Resources.HasFlag(HardshipResources.Screen))
            {
                _screenBusy["hardship." + request.Id] = 0;
            }
        });
        events.Subscribe<HardshipEnded>(ended =>
        {
            _screenBusy.TryRemove("hardship." + ended.Hardship.Id, out _);
            if (ended.Reason == HardshipEndReason.Survived)
            {
                React(PetMood.Happy, "hardship");
            }
        });
        events.Subscribe<RandomEventStarted>(started =>
        {
            if (started.Event.Resources.HasFlag(HardshipResources.Screen))
            {
                _screenBusy["event." + started.Event.Id] = 0;
            }
        });
        events.Subscribe<RandomEventEnded>(ended =>
        {
            _screenBusy.TryRemove("event." + ended.Event.Id, out _);
            if (ended.Reason == RandomEventEndReason.Completed)
            {
                React(PetMood.Happy, "event");
            }
        });

        events.Subscribe<QuestCompleted>(_ => React(PetMood.Happy, "quest"));
        events.Subscribe<AchievementUnlocked>(_ => React(PetMood.Happy, "achievement"));
        events.Subscribe<LevelReached>(_ => React(PetMood.Happy, "level"));
        events.Subscribe<UpgradePurchased>(_ => React(PetMood.Happy, "purchase"));
        events.Subscribe<ItemUnlocked>(_ => React(PetMood.Happy, "purchase"));
        events.Subscribe<CosmeticPurchased>(_ => React(PetMood.Happy, "purchase"));

        events.Publish(new HardshipsRequested());
        events.Publish(new HudBoundsRequested());
        events.Publish(new CosmeticsRequested());
        Publish();
    }

    public void Stop()
    {
        OverlayThread? overlay;
        lock (_lock)
        {
            overlay = _overlay;
            _overlay = null;
            _sprite = null;
            _shown = null;
        }

        overlay?.Dispose();
    }

    /// <summary>Текущее состояние для окна.</summary>
    internal PetState State()
    {
        lock (_lock)
        {
            var food = _species is null ? 0 : _food.GetValueOrDefault(_species.Code);
            return new PetState(
                _species?.Code,
                _enabled,
                _place,
                _rules.LevelFor(food),
                _rules.MaxLevel,
                food,
                _rules.NextFor(food),
                _rules.FeedPrice,
                _mood);
        }
    }

    internal void Publish() => _context?.Events.Publish(new PetChanged(State()));

    /// <summary>Кормит питомца. False — питомца нет, он выключен или не хватает монеток.</summary>
    internal bool Feed()
    {
        var context = _context!;
        string? reason = null;
        PetFed? fed = null;
        lock (_lock)
        {
            if (_species is null || !_enabled)
            {
                reason = "noPet";
            }
            else if (!context.Economy.TrySpend(_rules.FeedPrice, SpendReason))
            {
                reason = "coins";
            }
            else
            {
                var before = _rules.LevelFor(_food.GetValueOrDefault(_species.Code));
                var food = _food.GetValueOrDefault(_species.Code) + 1;
                _food[_species.Code] = food;
                context.Saves.Set(FoodKey, new Dictionary<string, long>(_food));
                var level = _rules.LevelFor(food);
                fed = new PetFed(_species.Code, _rules.FeedPrice, level, level > before);
                SetMoodLocked(PetMood.Eat, _rules.EatSeconds);
            }
        }

        if (fed is null)
        {
            context.Events.Publish(new PetFeedDenied(reason!));
            return false;
        }

        context.Log.Info($"Питомца покормили: {fed.Species}, уровень {fed.Level}{(fed.Grew ? ", вырос" : string.Empty)}");
        context.Events.Publish(new SaveRequested());
        context.Events.Publish(fed);
        context.Events.Publish(new PetReacted(PetMood.Eat, "feed"));
        Publish();
        return true;
    }

    internal void Configure(bool enabled, PetPlace place)
    {
        lock (_lock)
        {
            _enabled = enabled;
            _place = place;
            _context!.Settings.Set(EnabledSetting, enabled);
            _context.Settings.Set(PlaceSetting, place.ToString());
            _context.Log.Info($"Питомец: {(enabled ? "показан" : "выключен")}, место {place}");
        }

        UpdateOverlay();
        Publish();
    }

    /// <summary>Радость, еда или сон. Радость будит питомца, но не прерывает еду.</summary>
    internal void React(PetMood mood, string reason)
    {
        lock (_lock)
        {
            if (_species is null || !_enabled || (mood == PetMood.Happy && _mood == PetMood.Eat && _moodUntil > _clock.GetUtcNow()))
            {
                return;
            }

            SetMoodLocked(mood, mood == PetMood.Eat ? _rules.EatSeconds : _rules.HappySeconds);
        }

        _context?.Events.Publish(new PetReacted(mood, reason));
    }

    private void SetSpecies(CosmeticItem? species)
    {
        lock (_lock)
        {
            if (species?.Code == _species?.Code)
            {
                return;
            }

            _species = species;
            _frames = species is null
                ? []
                : species.Animations.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<Picture>)pair.Value.Select(rows => Picture.FromRows(rows, _colors)).ToList());
            _shown = null;
        }

        UpdateOverlay();
        Publish();
    }

    private void SetMoodLocked(PetMood mood, double seconds)
    {
        _mood = mood;
        _moodUntil = _clock.GetUtcNow().AddSeconds(seconds);
        _age = 0;
    }

    /// <summary>
    /// Поток окна питомца работает, только пока питомец есть и включён. Вызывается без замка: создание окна ждёт поток
    /// оверлея, а тот в шаге берёт этот замок.
    /// </summary>
    private void UpdateOverlay()
    {
        OverlayThread? stop = null;
        lock (_lock)
        {
            var wanted = _species is not null && _enabled;
            if (wanted == (_overlay is not null))
            {
                return;
            }

            if (!wanted)
            {
                stop = _overlay;
                _overlay = null;
                _sprite = null;
                _shown = null;
            }
        }

        if (stop is not null)
        {
            stop.Dispose();
            return;
        }

        var overlay = new OverlayThread("Pet", Tick, OnError, TickMs);
        var sprite = overlay.CreateSprite((_, _) => React(PetMood.Happy, "petted"));
        lock (_lock)
        {
            if (_overlay is null && _species is not null && _enabled)
            {
                _overlay = overlay;
                _sprite = sprite;
                _sinceCheck = CheckSeconds;
                return;
            }
        }

        overlay.Dispose();
    }

    private void Tick(double seconds)
    {
        SpriteOverlay? sprite;
        Picture? picture;
        int scale;
        ScreenPoint position;
        lock (_lock)
        {
            sprite = _sprite;
            if (sprite is null || _species is null)
            {
                return;
            }

            _sinceCheck += seconds;
            if (_sinceCheck >= CheckSeconds)
            {
                _sinceCheck = 0;
                Check();
            }

            if (_hiddenBecause is not null)
            {
                if (sprite.IsVisible)
                {
                    sprite.Hide();
                }

                return;
            }

            var now = _clock.GetUtcNow();
            if (_mood is PetMood.Happy or PetMood.Eat && now >= _moodUntil)
            {
                _mood = PetMood.Idle;
                _age = 0;
            }

            _age += seconds;
            var frames = _frames.GetValueOrDefault(MoodAnimation(_mood)) ?? _frames.GetValueOrDefault("idle") ?? [];
            if (frames.Count == 0)
            {
                return;
            }

            var fps = _species.Fps > 0 ? _species.Fps : _rules.Fps;
            picture = frames[(int)(_age * fps) % frames.Count];
            scale = _rules.ScaleFor(_food.GetValueOrDefault(_species.Code));
            position = Place(picture, scale, seconds);
        }

        if (!ReferenceEquals(picture, _shown) || scale != _shownScale || !sprite.IsVisible)
        {
            sprite.Show(picture.Pixels, picture.Width, picture.Height, scale, position);
            _shown = picture;
            _shownScale = scale;
        }
        else
        {
            sprite.MoveTo(position);
        }
    }

    /// <summary>Раз в секунду: можно ли показываться и не уснул ли человек.</summary>
    private void Check()
    {
        var quiet = _ignoreSafety ? null : QuietDetector.Check(watchCalls: false);
        _hiddenBecause = quiet is QuietDetector.Fullscreen or QuietDetector.SecureDesktop or QuietDetector.Presentation
            ? quiet
            : !_screenBusy.IsEmpty ? "screen" : null;

        var asleep = _idle() >= TimeSpan.FromMinutes(_rules.SleepMinutes);
        if (asleep && _mood == PetMood.Idle)
        {
            _mood = PetMood.Sleep;
            _age = 0;
            _context?.Events.Publish(new PetReacted(PetMood.Sleep, "idle"));
        }
        else if (!asleep && _mood == PetMood.Sleep)
        {
            _mood = PetMood.Idle;
            _age = 0;
        }
    }

    /// <summary>Рядом с HUD: слева от полоски, низом по её низу. Над панелью задач: бродит у правого края.</summary>
    private ScreenPoint Place(Picture picture, int scale, double seconds)
    {
        var width = picture.Width * scale;
        var height = picture.Height * scale;
        var area = OverlayArea.WorkArea();
        if (_place == PetPlace.Hud && _hud is { } bar)
        {
            var left = bar.Left - width - (HudGap * scale);
            if (left < area.Left)
            {
                left = bar.Left + bar.Width + (HudGap * scale);
            }

            return new ScreenPoint(left, bar.Top + bar.Height - height);
        }

        // Над панелью задач питомец медленно ходит туда-сюда, а спит и ест на месте.
        var range = Math.Max(0, TaskbarMargin - width);
        if (_mood is PetMood.Idle or PetMood.Happy && range > 0)
        {
            if (Math.Abs(_wander - _wanderTarget) < 1)
            {
                _wanderTarget = _random.NextDouble() * range;
            }

            var step = WanderSpeed * seconds * Math.Sign(_wanderTarget - _wander);
            _wander = Math.Abs(_wanderTarget - _wander) <= Math.Abs(step) ? _wanderTarget : _wander + step;
        }

        _wander = Math.Clamp(_wander, 0, range);
        return new ScreenPoint(area.Left + area.Width - TaskbarMargin + (int)_wander, area.Top + area.Height - height);
    }

    private static string MoodAnimation(PetMood mood) => mood switch
    {
        PetMood.Happy => "happy",
        PetMood.Eat => "eat",
        PetMood.Sleep => "sleep",
        _ => "idle",
    };

    private static IReadOnlyDictionary<char, uint> LoadColors(IModuleContext context)
    {
        var path = ADHDWindows.Modules.Hardships.Basic.AssetFiles.Find("palette.json");
        if (path is null)
        {
            return Colors.Keys;
        }

        try
        {
            return ADHDWindows.Core.Pixels.Palette.Parse(File.ReadAllText(path)).Colors.ToDictionary(color => color.Key, color => color.Argb);
        }
        catch (Exception exception) when (exception is IOException or FormatException or System.Text.Json.JsonException)
        {
            context.Log.Error("Палитра не прочитана, питомец рисуется запасными цветами", exception);
            return Colors.Keys;
        }
    }

    private void OnError(Exception exception)
    {
        if (Interlocked.Increment(ref _errors) <= 3)
        {
            _context?.Log.Error("Ошибка питомца", exception);
        }
    }
}
