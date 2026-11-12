using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ADHDWindows.Core.Cosmetics;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Progress;
using ADHDWindows.Core.Quests;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;
using ADHDWindows.Core.Upgrades;
using ADHDWindows.Modules.Cosmetics;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Modules.Pet;
using ADHDWindows.Modules.Sound;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

/// <summary>Данные репозитория и общие помощники проверок косметики.</summary>
internal static class CosmeticsRepo
{
    public static GameData Data(RecordingLog log)
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), log);
        data.Load();
        return data;
    }

    public static PaletteArt Palette() => PaletteArt.Load(new RecordingLog());

    public static IReadOnlyList<CosmeticItem> Items(RecordingLog log)
    {
        var palette = Palette();
        return CosmeticCatalog.Load(Data(log), log, palette.Keys.Keys.ToHashSet(), palette.Names.ToHashSet(StringComparer.Ordinal));
    }

    public static void WaitUntil(Func<bool> condition, string what, double seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(seconds), $"Не дождались: {what}");
            Thread.Sleep(30);
        }
    }

    /// <summary>Ядро с модулями в этом процессе. Данные — из репозитория, сохранения — в памяти.</summary>
    public sealed class Host : IDisposable
    {
        private readonly ModuleHost _host;

        public Host(RecordingLog log, IKeyValueStore saves, IKeyValueStore settings, string recovery, bool developerMode, params IModule[] modules)
        {
            Bus = new EventBus(log);
            Economy = new CoinEconomy(saves, Bus, TimeProvider.System);
            var data = Data(log);
            var texts = new TextCatalog(data, "ru", log);
            Journal = new ChangeJournal(recovery, log);
            Recovery = new RecoveryService(Journal, () => [new CursorSkinRestorer(), new CursorRestorer()], log);
            _host = new ModuleHost(Bus, data, texts, Economy, saves, settings, Recovery, log, developerMode);
            _host.Start(modules);
        }

        public EventBus Bus { get; }

        public CoinEconomy Economy { get; }

        public RecoveryService Recovery { get; }

        public ChangeJournal Journal { get; }

        public void Dispose() => _host.StopAll();
    }
}

public sealed class CosmeticCatalogTests
{
    [Fact]
    public void Repository_data_is_clean_and_every_item_has_texts_and_a_source()
    {
        var log = new RecordingLog();
        var items = CosmeticsRepo.Items(log);
        Assert.True(log.Warnings.Count == 0 && log.Errors.Count == 0, string.Join(Environment.NewLine, log.Warnings.Concat(log.Errors)));

        var bySlot = items.GroupBy(item => item.Slot).ToDictionary(group => group.Key, group => group.Count());
        Assert.True(bySlot[CosmeticSlot.Cursor] >= 6);
        Assert.True(bySlot[CosmeticSlot.Theme] >= 4);
        Assert.True(bySlot[CosmeticSlot.Coin] >= 4);
        Assert.True(bySlot[CosmeticSlot.Trail] >= 2 && bySlot[CosmeticSlot.Click] >= 2 && bySlot[CosmeticSlot.Purchase] >= 2);
        Assert.True(bySlot[CosmeticSlot.Pet] >= 3);
        Assert.Single(items, item => item.Slot == CosmeticSlot.Pet && item.Starter);

        var data = CosmeticsRepo.Data(log);
        var texts = new TextCatalog(data, "ru", log);
        texts.Use("ru");
        foreach (var item in items)
        {
            Assert.NotEqual($"cosmetics.{item.Code}.title", texts.Get($"cosmetics.{item.Code}.title"));
            Assert.NotEqual($"cosmetics.{item.Code}.text", texts.Get($"cosmetics.{item.Code}.text"));

            // Каждый предмет можно получить: купить, открыть уровнем, найти в сундуке, получить с начала или от сезона.
            Assert.True(item.ForSale || item.UnlockLevel > 0 || item.Chest || item.Starter || item.Slot == CosmeticSlot.Theme, item.Code);
            if (item.Slot == CosmeticSlot.Pet)
            {
                Assert.All(new[] { "idle", "happy", "eat", "sleep" }, animation => Assert.True(item.Animations.ContainsKey(animation), $"{item.Code}: {animation}"));
            }
        }

        // Косметика, которую обещают звания и сезоны, есть в данных.
        var codes = items.Select(item => item.Code).ToHashSet();
        var levels = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "data", "progress", "levels.json"));
        var seasons = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "data", "events", "seasons.json"));
        var promised = System.Text.RegularExpressions.Regex.Matches(levels + seasons, "\"cosmetic\": \"([^\"]+)\"").Select(match => match.Groups[1].Value).ToList();
        Assert.True(promised.Count >= 8);
        Assert.All(promised, code => Assert.Contains(code, codes));
        var rankItems = items.Where(item => item.UnlockLevel > 0).ToList();
        Assert.All(rankItems, item => Assert.False(item.ForSale, item.Code));

        // Правила питомца и звуки читаются без замечаний, файлы звуков есть и открываются.
        Assert.NotSame(PetRules.Default, PetRules.Load(data, log));
        var sound = SoundData.Load(data, log);
        Assert.True(sound.Cues.Count >= 10);
        foreach (var file in sound.Cues.Values.Select(cue => cue.File).Concat(sound.Music))
        {
            var samples = WaveFile.Read(Path.Combine(RepositoryRoot.Find(), "assets", "sounds", file));
            Assert.True(samples.Length > 1000, file);
            Assert.Contains(samples, sample => Math.Abs((int)sample) > 4000);
        }

        Assert.Empty(log.Warnings);
    }

    [Fact]
    public void Broken_items_are_skipped_with_a_warning()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("cosmetics"));
        File.WriteAllText(temp.Combine("cosmetics", "cursors.json"), """
            { "items": [
              { "code": "cursor.ok", "price": 10, "art": ["KK", "KWK"] },
              { "code": "cursor.ok", "price": 10, "art": ["KK"] },
              { "code": "cursor.badColor", "price": 10, "art": ["KZ"] },
              { "code": "cursor.huge", "price": 10, "art": ["KKKKKKKKKKKKKKKKK"] },
              { "code": "cursor.noArt", "price": 10 },
              { "price": 10, "art": ["K"] }
            ] }
            """);
        File.WriteAllText(temp.Combine("cosmetics", "themes.json"), """
            { "items": [ { "code": "theme.bad", "colors": { "panel": "red" } }, { "code": "theme.unknown", "colors": { "sky": "#FFFFFF" } } ] }
            """);
        File.WriteAllText(temp.Combine("cosmetics", "effects.json"), """{ "items": [ { "code": "x.y", "slot": "nowhere", "frames": [["K"]] } ] }""");
        var log = new RecordingLog();
        var data = new GameData(temp.Path, log);
        data.Load();
        var palette = CosmeticsRepo.Palette();

        var items = CosmeticCatalog.Load(data, log, palette.Keys.Keys.ToHashSet(), palette.Names.ToHashSet(StringComparer.Ordinal));

        Assert.Equal(["cursor.ok"], items.Select(item => item.Code));
        Assert.True(log.Warnings.Count >= 8, string.Join(Environment.NewLine, log.Warnings));
    }
}

public sealed class WardrobeTests
{
    private readonly RecordingLog _log = new();
    private readonly IReadOnlyList<CosmeticItem> _items;
    private readonly MemoryStore _saves = new();
    private readonly MemoryStore _settings = new();
    private readonly EventBus _bus;
    private readonly CoinEconomy _economy;
    private readonly ConcurrentQueue<object> _seen = new();

    public WardrobeTests()
    {
        _items = CosmeticsRepo.Items(_log);
        _bus = new EventBus(_log);
        _economy = new CoinEconomy(_saves, _bus, TimeProvider.System);
        _bus.Subscribe<CosmeticPurchased>(_seen.Enqueue);
        _bus.Subscribe<CosmeticPurchaseDenied>(_seen.Enqueue);
        _bus.Subscribe<CosmeticGranted>(_seen.Enqueue);
    }

    private Wardrobe Create() => new(_items, _economy, _bus, _saves, _settings, TimeProvider.System, _log);

    [Fact]
    public void Buying_spends_coins_equips_the_item_and_survives_a_restart_without_touching_income()
    {
        var wardrobe = Create();
        var sword = _items.Single(item => item.Code == "cursor.sword");
        _economy.Earn("developer", sword.Price + 5);
        var income = _economy.Preview("click", 10);

        Assert.Null(wardrobe.Buy(sword.Code));

        Assert.Equal(5, _economy.Balance);
        Assert.True(wardrobe.Owns(sword.Code));
        Assert.Equal(sword.Code, wardrobe.Equipped(CosmeticSlot.Cursor)?.Code);
        Assert.Single(_seen.OfType<CosmeticPurchased>());
        Assert.Equal(income, _economy.Preview("click", 10));

        var restarted = Create();
        Assert.True(restarted.Owns(sword.Code));
        Assert.Equal(sword.Code, restarted.Equipped(CosmeticSlot.Cursor)?.Code);
        Assert.True(restarted.Snapshot().Items.Single(offer => offer.Item.Code == sword.Code).Equipped);
    }

    [Fact]
    public void Purchase_is_refused_for_unknown_owned_unsold_and_unaffordable_items()
    {
        var wardrobe = Create();
        _economy.Earn("developer", 100);

        Assert.Equal(CosmeticDenial.Unknown, wardrobe.Buy("cursor.nothing"));
        Assert.Equal(CosmeticDenial.NotForSale, wardrobe.Buy("cursor.feather"));
        Assert.Equal(CosmeticDenial.NotForSale, wardrobe.Buy("cursor.gold"));
        Assert.Equal(CosmeticDenial.Owned, wardrobe.Buy("pet.blob"));
        Assert.Equal(CosmeticDenial.NotEnoughCoins, wardrobe.Buy("cursor.wand"));

        Assert.Equal(100, _economy.Balance);
        Assert.Equal(5, _seen.OfType<CosmeticPurchaseDenied>().Count());
    }

    [Fact]
    public void Grants_levels_and_availability_follow_ownership()
    {
        var wardrobe = Create();

        // Первый питомец есть и надет с начала.
        Assert.Equal("pet.blob", wardrobe.Equipped(CosmeticSlot.Pet)?.Code);
        var available = wardrobe.Available();
        Assert.DoesNotContain("pet.blob", available.Codes);
        Assert.Contains("theme.halloween", available.Codes);
        Assert.DoesNotContain("theme.halloween", available.ChestCodes!);
        Assert.DoesNotContain("cursor.gold", available.ChestCodes!);
        Assert.Contains("cursor.feather", available.ChestCodes!);

        Assert.True(wardrobe.Grant("cursor.feather", "chest.rare"));
        Assert.False(wardrobe.Grant("cursor.feather", "chest.rare"));
        Assert.False(wardrobe.Grant("cursor.nothing", "chest.rare"));
        Assert.DoesNotContain("cursor.feather", wardrobe.Available().Codes);
        Assert.Null(wardrobe.Equipped(CosmeticSlot.Cursor));

        wardrobe.ReachLevel(9);
        Assert.False(wardrobe.Owns("pet.fox"));
        wardrobe.ReachLevel(12);
        Assert.True(wardrobe.Owns("pet.fox"));
        Assert.False(wardrobe.Owns("cursor.gold"));
        Assert.Equal(2, _seen.OfType<CosmeticGranted>().Count());

        // Можно надеть только своё и только на своё место. Снятый питомец не возвращается после перезапуска.
        Assert.False(wardrobe.Equip(CosmeticSlot.Cursor, "cursor.sword"));
        Assert.False(wardrobe.Equip(CosmeticSlot.Theme, "cursor.feather"));
        Assert.True(wardrobe.Equip(CosmeticSlot.Cursor, "cursor.feather"));
        Assert.True(wardrobe.Equip(CosmeticSlot.Pet, null));
        Assert.Null(Create().Equipped(CosmeticSlot.Pet));
        Assert.Equal("cursor.feather", Create().Equipped(CosmeticSlot.Cursor)?.Code);
    }

    [Fact]
    public void Module_grants_rank_cosmetics_and_answers_chests()
    {
        using var temp = new TempDirectory();
        var module = new CosmeticsModule();
        using var host = new CosmeticsRepo.Host(_log, _saves, _settings, temp.Combine("recovery.json"), false, module);
        CosmeticsAvailable? answer = null;
        host.Bus.Subscribe<CosmeticsAvailable>(available => answer = available);

        host.Bus.Publish(new LevelReached(20, "survivor", "Покоритель", true, 0, "cursor.gold", false));
        host.Bus.Publish(new CosmeticGrantRequested("theme.halloween", "season.halloween"));
        host.Bus.Publish(new CosmeticsAvailableRequested());

        Assert.True(module.Wardrobe!.Owns("cursor.gold"));
        Assert.True(module.Wardrobe.Owns("pet.fox"), "уровень 20 открывает и то, что было до него");
        Assert.True(module.Wardrobe.Owns("theme.halloween"));
        Assert.NotNull(answer);
        Assert.DoesNotContain("theme.halloween", answer.Codes);
        Assert.Empty(_log.Errors);
    }
}

/// <summary>Скин меняет курсор всей системы, поэтому проверки идут вместе с другими такими, не параллельно.</summary>
[Collection(nameof(EmergencyStopTests))]
public sealed class CursorSkinTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();

    public void Dispose()
    {
        CursorControl.RestoreCursors();
        _temp.Dispose();
    }

    [Fact]
    public void Cursor_skin_gives_way_to_cursor_hardships_and_comes_back_after_them()
    {
        var original = CursorControl.VisiblePixelsOfArrow();
        Assert.True(original > 0);
        var saves = new MemoryStore();
        var cosmetics = new CosmeticsModule();
        var invisible = new InvisibleCursorHardship();
        var host = new CosmeticsRepo.Host(_log, saves, new MemoryStore(), _temp.Combine("recovery.json"), false, invisible, cosmetics);
        try
        {
            var sword = cosmetics.Wardrobe!.Snapshot().Items.Single(offer => offer.Item.Code == "cursor.sword").Item;
            var skin = cosmetics.Art(sword)!.Frames[0];
            var size = CursorControl.SystemCursorSize();
            var expected = CursorSkin.Scale(skin, sword, size).Pixels.Count(pixel => pixel >> 24 != 0);
            host.Economy.Earn("developer", sword.Price);

            host.Bus.Publish(new CosmeticPurchaseRequested(sword.Code));

            Assert.True(cosmetics.Skin!.Installed);
            Assert.Equal(expected, CursorControl.VisiblePixelsOfArrow());
            Assert.Contains(host.Journal.Read(), change => change.Kind == CursorSkinRestorer.ChangeKind);

            // Невидимый курсор важнее скина: курсор пропадает, а после конца усложнения скин возвращается.
            var info = invisible.Info;
            host.Bus.Publish(new HardshipStartRequested(info.Id));
            Assert.Equal(0, CursorControl.VisiblePixelsOfArrow());
            Assert.True(cosmetics.Skin.Paused);
            Assert.True(cosmetics.Effects!.Paused);

            // Снять скин во время усложнения нельзя так, чтобы курсор стал виден.
            host.Bus.Publish(new CosmeticEquipRequested(CosmeticSlot.Cursor, null));
            Assert.Equal(0, CursorControl.VisiblePixelsOfArrow());
            host.Bus.Publish(new CosmeticEquipRequested(CosmeticSlot.Cursor, sword.Code));

            host.Bus.Publish(new HardshipStopRequested(info.Id));
            host.Bus.Publish(new HardshipEnded(info, HardshipEndReason.Survived, 0));
            Assert.Equal(expected, CursorControl.VisiblePixelsOfArrow());
            Assert.False(cosmetics.Effects.Paused);

            // Снятый скин возвращает курсор пользователя и запись в реестре.
            host.Bus.Publish(new CosmeticEquipRequested(CosmeticSlot.Cursor, null));
            Assert.Equal(original, CursorControl.VisiblePixelsOfArrow());
            Assert.DoesNotContain(host.Journal.Read(), change => change.Kind == CursorSkinRestorer.ChangeKind);

            host.Bus.Publish(new CosmeticEquipRequested(CosmeticSlot.Cursor, sword.Code));
            Assert.Equal(expected, CursorControl.VisiblePixelsOfArrow());
        }
        finally
        {
            host.Dispose();
        }

        // Остановка программы снимает скин.
        Assert.Equal(original, CursorControl.VisiblePixelsOfArrow());
        Assert.Empty(_log.Errors);
    }
}

public sealed class EffectsTests
{
    [Fact]
    public void Effects_play_on_the_screen_and_disappear_and_pause_on_request()
    {
        var log = new RecordingLog();
        var palette = CosmeticsRepo.Palette();
        var items = CosmeticsRepo.Items(log).ToDictionary(item => item.Code);
        EffectArt Art(string code) => new(items[code].Frames(CosmeticItem.DefaultAnimation).Select(palette.Render).ToList(), items[code].Fps);
        var area = TestScreen.Find()?.WorkArea ?? new ScreenRect(0, 0, 1280, 720);
        var center = new ScreenPoint(area.Left + (area.Width / 2), area.Top + (area.Height / 2));
        using var effects = new EffectsPlayer(log);
        Assert.False(effects.Running);

        effects.Configure(null, Art("click.burst"), Art("purchase.fireworks"));
        Assert.True(effects.Running);
        effects.Click(center);
        CosmeticsRepo.WaitUntil(() => effects.Visible == 1, "вспышка клика");
        CosmeticsRepo.WaitUntil(() => effects.Visible == 0, "вспышка погасла");

        effects.Celebrate(area);
        CosmeticsRepo.WaitUntil(() => effects.Visible >= 3, "фейерверк");
        Assert.True(effects.Windows <= EffectsPlayer.MaxParticles);
        CosmeticsRepo.WaitUntil(() => effects.Visible == 0, "фейерверк закончился");

        effects.Paused = true;
        effects.Click(center);
        effects.Celebrate(area);
        Thread.Sleep(200);
        Assert.Equal(0, effects.Visible);

        effects.Configure(null, null, null);
        Assert.False(effects.Running);
        Assert.Empty(log.Errors);
    }
}

public sealed class PetTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly MemoryStore _saves = new();
    private readonly MemoryStore _settings = new();
    private TimeSpan _idle;

    public PetTests()
    {
        // Полноэкранное окно на основном мониторе не должно прятать питомца в проверках.
        _settings.Set($"modules/pet/{PetModule.IgnoreSafetySetting}", true);
    }

    public void Dispose() => _temp.Dispose();

    private (CosmeticsRepo.Host Host, PetModule Pet, CosmeticsModule Cosmetics) Start()
    {
        var cosmetics = new CosmeticsModule();
        var pet = new PetModule(TimeProvider.System, () => _idle);
        var host = new CosmeticsRepo.Host(_log, _saves, _settings, _temp.Combine("recovery.json"), true, cosmetics, pet);
        return (host, pet, cosmetics);
    }

    [Fact]
    public void Pet_sits_by_the_hud_moves_to_the_taskbar_and_disappears_when_turned_off()
    {
        var (host, pet, _) = Start();
        try
        {
            var area = TestScreen.Find()?.WorkArea ?? new ScreenRect(0, 0, 1280, 720);
            var bar = new ScreenRect(area.Left + area.Width - 300, area.Top + 40, 200, 30);
            host.Bus.Publish(new HudMoved(bar, true));

            CosmeticsRepo.WaitUntil(() => pet.Visible, "питомец появился");
            var bounds = pet.Bounds!.Value;
            Assert.Equal(bar.Top + bar.Height, bounds.Top + bounds.Height);
            Assert.True(bounds.Left + bounds.Width <= bar.Left, "питомец слева от HUD");

            host.Bus.Publish(new PetSettingsRequested(true, PetPlace.Taskbar));
            CosmeticsRepo.WaitUntil(() => pet.Bounds is { } place && place.Top + place.Height == area.Top + area.Height, "питомец над панелью задач");

            // Пока на экране усложнение, питомец прячется.
            host.Bus.Publish(new HardshipOffered(new HardshipInfo("snow", HardshipLevel.Easy, HardshipResources.Screen)));
            host.Bus.Publish(new HardshipStartRequested("snow"));
            CosmeticsRepo.WaitUntil(() => !pet.Visible, "питомец спрятался от усложнения");
            host.Bus.Publish(new HardshipEnded(new HardshipInfo("snow", HardshipLevel.Easy, HardshipResources.Screen), HardshipEndReason.Survived, 0));
            CosmeticsRepo.WaitUntil(() => pet.Visible, "питомец вернулся");
            Assert.Equal(PetMood.Happy, pet.Mood);

            host.Bus.Publish(new PetSettingsRequested(false, PetPlace.Taskbar));
            Assert.False(pet.Running);
            Assert.False(_settings.Get($"modules/pet/{PetModule.EnabledSetting}", true));

            // Без вида питомца окна нет даже при включённом питомце.
            host.Bus.Publish(new PetSettingsRequested(true, PetPlace.Hud));
            Assert.True(pet.Running);
            host.Bus.Publish(new CosmeticEquipRequested(CosmeticSlot.Pet, null));
            Assert.False(pet.Running);
        }
        finally
        {
            host.Dispose();
        }

        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Feeding_costs_coins_grows_the_pet_and_is_remembered()
    {
        var (host, pet, _) = Start();
        var fed = new List<PetFed>();
        var denied = new List<PetFeedDenied>();
        PetState? state = null;
        host.Bus.Subscribe<PetFed>(fed.Add);
        host.Bus.Subscribe<PetFeedDenied>(denied.Add);
        host.Bus.Subscribe<PetChanged>(change => state = change.State);
        try
        {
            var price = PetRules.Default.FeedPrice;
            host.Economy.Earn("developer", price * 3);

            for (var i = 0; i < 4; i++)
            {
                host.Bus.Publish(new PetFeedRequested());
            }

            Assert.Equal(3, fed.Count);
            Assert.Equal([false, false, true], fed.Select(item => item.Grew));
            Assert.Single(denied);
            Assert.Equal(0, host.Economy.Balance);
            Assert.Equal(PetMood.Eat, pet.Mood);
            Assert.Equal(new PetState("pet.blob", true, PetPlace.Hud, 2, 5, 3, 8, price, PetMood.Eat), state);

            // Радость не прерывает еду, а сон приходит, когда человек отошёл.
            host.Bus.Publish(new QuestCompleted(null!, 0, false));
            Assert.Equal(PetMood.Eat, pet.Mood);
        }
        finally
        {
            host.Dispose();
        }

        (host, pet, _) = Start();
        try
        {
            Assert.Equal(2, pet.State().Level);
            Assert.Equal(3, pet.State().Food);

            host.Bus.Publish(new QuestCompleted(null!, 0, false));
            Assert.Equal(PetMood.Happy, pet.Mood);
            host.Bus.Publish(new HudMoved(new ScreenRect(0, 0, 10, 10), false));
            _idle = TimeSpan.FromMinutes(10);
            CosmeticsRepo.WaitUntil(() => pet.Mood == PetMood.Sleep, "питомец уснул", 15);
            _idle = TimeSpan.Zero;
            CosmeticsRepo.WaitUntil(() => pet.Mood == PetMood.Idle, "питомец проснулся");
        }
        finally
        {
            host.Dispose();
        }

        Assert.Empty(_log.Errors);
    }
}

public sealed class SoundTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly MemoryStore _settings = new();
    private readonly FakeOutput _output = new();
    private readonly ManualClock _clock = new();
    private TimeSpan _idle;

    public void Dispose() => _temp.Dispose();

    private (CosmeticsRepo.Host Host, SoundModule Sound) Start()
    {
        var sound = new SoundModule(() => _output, () => _idle, _clock);
        return (new CosmeticsRepo.Host(_log, new MemoryStore(), _settings, _temp.Combine("recovery.json"), false, sound), sound);
    }

    [Fact]
    public void Game_events_play_their_sounds_unless_muted_or_quiet()
    {
        var (host, sound) = Start();
        var upgrade = new UpgradePurchased("click", "Клик", 1, 10, DateTimeOffset.UtcNow);

        host.Bus.Publish(upgrade);
        host.Bus.Publish(upgrade);
        Assert.Single(_output.Played);
        Assert.Equal(SoundModule.Loudness(SoundSettings.Default.Volume) * 0.8f, _output.Played[0].Volume, 3);

        _clock.Advance(TimeSpan.FromSeconds(1));
        host.Bus.Publish(new SoundCueRequested("quest"));
        host.Bus.Publish(new SoundCueRequested("nothing"));
        Assert.Equal(2, _output.Played.Count);

        // «Не беспокоить» и полноэкранное окно глушат звук.
        _clock.Advance(TimeSpan.FromSeconds(1));
        host.Bus.Publish(Status(quietUntil: _clock.GetUtcNow().AddMinutes(30)));
        host.Bus.Publish(new SoundCueRequested("quest"));
        host.Bus.Publish(Status(blocked: "fullscreen"));
        host.Bus.Publish(new SoundCueRequested("quest"));
        Assert.Equal(2, _output.Played.Count);
        host.Bus.Publish(Status());
        host.Bus.Publish(new SoundCueRequested("quest"));
        Assert.Equal(3, _output.Played.Count);

        // Выключенный звук молчит, а настройка переживает перезапуск.
        _clock.Advance(TimeSpan.FromSeconds(1));
        host.Bus.Publish(new SoundSettingsChangeRequested(sound.Settings with { Effects = false }));
        host.Bus.Publish(new SoundCueRequested("level"));
        Assert.Equal(3, _output.Played.Count);
        Assert.True(_output.Silenced > 0);
        host.Dispose();

        (host, sound) = Start();
        using var restarted = host;
        Assert.False(sound.Settings.Effects);
        host.Bus.Publish(new SoundCueRequested("level"));
        Assert.Equal(3, _output.Played.Count);
        Assert.Empty(_log.Errors);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Music_plays_only_when_enabled_and_nobody_needs_quiet()
    {
        var (host, sound) = Start();
        using var _ = host;
        Assert.Null(_output.Music);

        host.Bus.Publish(new SoundSettingsChangeRequested(sound.Settings with { Music = true, MusicVolume = 50 }));
        Assert.NotNull(_output.Music);
        Assert.Equal(0.25f, _output.MusicVolume, 3);
        Assert.True(sound.MusicPlaying);

        host.Bus.Publish(Status(blocked: "call"));
        Assert.Null(_output.Music);
        host.Bus.Publish(Status());
        Assert.NotNull(_output.Music);

        _idle = TimeSpan.FromMinutes(SoundModule.AwayMinutes + 1);
        sound.CheckAwayForTest();
        Assert.Null(_output.Music);
        _idle = TimeSpan.Zero;
        sound.CheckAwayForTest();
        Assert.NotNull(_output.Music);

        host.Bus.Publish(new SoundSettingsChangeRequested(sound.Settings with { Music = false }));
        Assert.Null(_output.Music);
    }

    [Fact]
    public void Mixer_plays_silence_and_closes_the_device_afterwards()
    {
        using var mixer = new SoundMixer();
        mixer.Play(new short[SoundMixer.SampleRate / 4], 1);
        CosmeticsRepo.WaitUntil(() => mixer.IsOpen || mixer.Failed, "устройство открыто");
        if (mixer.Failed)
        {
            // На компьютере без звука программа просто молчит.
            return;
        }

        CosmeticsRepo.WaitUntil(() => !mixer.IsOpen, "устройство закрыто в тишине");
    }

    private static HardshipStatus Status(DateTimeOffset? quietUntil = null, string? blocked = null) =>
        new(HardshipMode.Normal, 1, [], null, null, null, quietUntil, blocked);

    private sealed class FakeOutput : ISoundOutput
    {
        public List<(int Length, float Volume)> Played { get; } = [];

        public short[]? Music { get; private set; }

        public float MusicVolume { get; private set; }

        public int Silenced { get; private set; }

        public void Play(short[] samples, float volume) => Played.Add((samples.Length, volume));

        public void SetMusic(short[]? samples, float volume)
        {
            Music = samples;
            MusicVolume = volume;
        }

        public void Silence() => Silenced++;

        public void Dispose()
        {
        }
    }
}
