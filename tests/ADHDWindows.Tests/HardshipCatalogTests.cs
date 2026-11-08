using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Hardships;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Modules.Hardships.Cursor;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

/// <summary>Логика усложнений этапа 15 без системы: движение курсора, частицы, картинки.</summary>
public sealed class HardshipPartsTests
{
    [Fact]
    public void Wind_drifts_at_its_speed_and_turns_slowly()
    {
        var wind = new WindDrift(new Random(1), 30);
        double x = 0;
        double y = 0;
        var angle = wind.Angle;
        for (var i = 0; i < 60; i++)
        {
            var (dx, dy) = wind.Step(1 / 60.0, 0, 0, default);
            x += dx;
            y += dy;
        }

        var distance = Math.Sqrt((x * x) + (y * y));
        Assert.InRange(distance, 20, 30.01);
        Assert.True(Math.Abs(wind.Angle - angle) < Math.PI / 2, "за секунду ветер не разворачивается");
    }

    [Fact]
    public void Ice_keeps_sliding_after_the_hand_stops_and_then_stops()
    {
        var ice = new IceSlide();
        for (var i = 0; i < 30; i++)
        {
            ice.Step(1 / 60.0, 10, 0, default);
        }

        var (slide, _) = ice.Step(1 / 60.0, 0, 0, default);
        Assert.True(slide > 3, "после остановки руки курсор едет дальше");
        double rest = 0;
        for (var i = 0; i < 300; i++)
        {
            rest = ice.Step(1 / 60.0, 0, 0, default).Dx;
        }

        Assert.True(rest < 0.05, "трение гасит скольжение");
        ice.Reset();
        Assert.Equal(0, ice.Speed);
    }

    [Fact]
    public void Tremor_and_wobble_stay_around_the_place()
    {
        var tremor = new Tremor(new Random(2));
        var wobble = new Wobble(new Random(3));
        double tx = 0;
        double ty = 0;
        double wx = 0;
        double wy = 0;
        var moved = false;
        for (var i = 0; i < 600; i++)
        {
            var (dx, dy) = tremor.Step(1 / 60.0, 0, 0, default);
            moved |= dx != 0 || dy != 0;
            tx += dx;
            ty += dy;
            Assert.InRange(tx, -Tremor.DefaultAmplitude, Tremor.DefaultAmplitude);
            Assert.InRange(ty, -Tremor.DefaultAmplitude, Tremor.DefaultAmplitude);
            (dx, dy) = wobble.Step(1 / 60.0, 0, 0, default);
            wx += dx;
            wy += dy;
            Assert.InRange(wx, -Wobble.DefaultRadius - 0.01, Wobble.DefaultRadius + 0.01);
            Assert.InRange(wy, -Wobble.DefaultRadius - 0.01, Wobble.DefaultRadius + 0.01);
        }

        Assert.True(moved);
    }

    [Fact]
    public void Teleport_jumps_a_short_distance_every_few_seconds()
    {
        var teleport = new Teleport(new Random(4));
        var jumps = new List<(double Time, double Distance)>();
        for (var i = 0; i < 60 * 30; i++)
        {
            var (dx, dy) = teleport.Step(1 / 60.0, 0, 0, default);
            if (dx != 0 || dy != 0)
            {
                jumps.Add((i / 60.0, Math.Sqrt((dx * dx) + (dy * dy))));
            }
        }

        Assert.InRange(jumps.Count, 30 / (int)Teleport.MaxPause, (int)(30 / Teleport.MinPause) + 1);
        Assert.All(jumps, jump => Assert.InRange(jump.Distance, Teleport.MinJump - 1, Teleport.MaxJump + 1));
    }

    [Fact]
    public void Magnet_pulls_towards_its_point()
    {
        var screen = new ScreenRect(0, 0, 1920, 1080);
        var magnet = new Magnet(new Random(5), _ => screen);
        var position = new ScreenPoint(0, 0);
        magnet.Step(1 / 60.0, 0, 0, position);
        var target = magnet.Target!.Value;
        Assert.True(screen.Contains(target));
        var before = Distance(position, target);
        var (dx, dy) = magnet.Step(1, 0, 0, position);
        var after = Distance(new ScreenPoint(position.X + (int)dx, position.Y + (int)dy), target);
        Assert.True(after < before - 20);
    }

    [Fact]
    public void Glass_breaks_only_on_fast_moves_and_returns_back()
    {
        var glass = new GlassGuard();
        var time = 0.0;
        var x = 100;

        // Спокойно: 600 пикселей в секунду.
        for (var i = 0; i < 120; i++)
        {
            time += 1 / 60.0;
            x += 10;
            Assert.Null(glass.Step(time, new ScreenPoint(x, 100)));
        }

        // Рывок: 6000 пикселей в секунду.
        var start = x;
        ScreenPoint? back = null;
        for (var i = 0; i < 20 && back is null; i++)
        {
            time += 1 / 60.0;
            x += 100;
            back = glass.Step(time, new ScreenPoint(x, 100));
        }

        Assert.NotNull(back);
        Assert.True(back.Value.X <= start, "курсор возвращается туда, где был до рывка");
        Assert.Equal(1, glass.Breaks);
    }

    [Fact]
    public void Lag_shows_where_the_cursor_was_a_moment_ago()
    {
        var lag = new LagLine(0.25);
        lag.Start(new ScreenPoint(0, 0));
        lag.Add(0.0, new ScreenPoint(10, 0));
        lag.Add(0.1, new ScreenPoint(20, 0));
        Assert.Equal(new ScreenPoint(0, 0), lag.At(0.2));
        Assert.Equal(new ScreenPoint(10, 0), lag.At(0.3));
        Assert.Equal(new ScreenPoint(20, 0), lag.At(0.5));
    }

    [Fact]
    public void Filters_invert_and_mirror_the_movement()
    {
        var inversion = new InversionHardship();
        var mirror = new MirrorHardship();
        var current = new ScreenPoint(500, 500);
        var target = new ScreenPoint(520, 490);

        Assert.Equal(new ScreenPoint(480, 510), inversion.Apply(current, target));
        Assert.Equal(new ScreenPoint(480, 490), mirror.Apply(current, target));

        // Смещение берётся без обрезки краем экрана, поэтому у края инверсия тоже уводит курсор обратно.
        Assert.Equal(new ScreenPoint(30, 500), inversion.Apply(new ScreenPoint(0, 500), new ScreenPoint(-30, 500)));
    }

    [Fact]
    public void Clamp_keeps_the_cursor_on_a_monitor()
    {
        IReadOnlyList<ScreenArea> screens =
        [
            new(new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040), 96, true),
            new(new ScreenRect(1920, 174, 1600, 900), new ScreenRect(1920, 174, 1600, 860), 96, false),
        ];

        Assert.Equal(new ScreenPoint(2000, 174), ScreenBounds.Clamp(screens, new ScreenPoint(2000, 50)));
        Assert.Equal(new ScreenPoint(0, 1079), ScreenBounds.Clamp(screens, new ScreenPoint(-40, 2000)));
        Assert.Equal(new ScreenPoint(100, 100), ScreenBounds.Clamp(screens, new ScreenPoint(100, 100)));
    }

    [Fact]
    public void Pixel_circle_is_round_and_stepped()
    {
        var center = new ScreenPoint(500, 400);
        var rects = PixelShapes.Circle(center, 100, 8);
        var area = rects.Sum(rect => (long)rect.Width * rect.Height);

        Assert.InRange(area, (long)(Math.PI * 100 * 100 * 0.85), (long)(Math.PI * 100 * 100 * 1.02));
        Assert.All(rects, rect =>
        {
            Assert.Equal(8, rect.Height);
            Assert.Equal(0, rect.Width % 16);
            Assert.Equal(center.X, rect.Left + (rect.Width / 2));
        });
        Assert.Equal(rects.First().Width, rects.Last().Width);
    }

    [Fact]
    public void Falling_particles_fill_the_screen_and_stay_on_it()
    {
        var small = new ScreenRect(0, 0, 1280, 720);
        var large = new ScreenRect(0, 0, 2560, 1440);
        var rain = new FallingField(new Random(6), FallSettings.Rain, small);
        var count = rain.Count;
        rain.Resize(large);
        Assert.True(rain.Count > count * 3, "на большом экране капель больше");

        var output = new List<ScreenRect>();
        for (var i = 0; i < 300; i++)
        {
            output.Clear();
            rain.Step(1 / 60.0, output);
        }

        Assert.Equal(rain.Count, output.Count);
        Assert.All(output, drop => Assert.InRange(drop.Top, large.Top - 100, large.Bottom));
    }

    [Fact]
    public void Coin_dust_appears_only_while_moving_and_fades()
    {
        var dust = new CoinDust(new Random(7));
        var output = new List<ScreenRect>();
        dust.Step(1 / 60.0, new ScreenPoint(100, 100), output);
        for (var i = 0; i < 30; i++)
        {
            dust.Step(1 / 60.0, new ScreenPoint(100, 100), output);
        }

        Assert.Equal(0, dust.Count);
        for (var i = 1; i <= 30; i++)
        {
            dust.Step(1 / 60.0, new ScreenPoint(100 + (i * 15), 100), output);
        }

        Assert.True(dust.Count > 10);
        for (var i = 0; i < 70; i++)
        {
            dust.Step(1 / 60.0, new ScreenPoint(550, 100), output);
        }

        Assert.Equal(0, dust.Count);
    }

    [Fact]
    public void Ad_has_text_and_a_close_box_in_the_corner()
    {
        var font = OverlayFont.Body(new RecordingLog());
        Assert.NotNull(font);
        var ad = AdsHardship.Draw("Купи 1000 монеток всего за 999 монеток!", font);

        Assert.Equal(AdsHardship.ArtWidth, ad.Width);
        Assert.True(AdsHardship.IsClose(ad.Width - 4, 4));
        Assert.False(AdsHardship.IsClose(10, 30));
        Assert.Equal(0xFFFFFFFF, ad[ad.Width - 1 - 11 + 2, 3]);
        Assert.True(ad.Pixels.Count(pixel => pixel == 0xFFF4EFE0) > 100, "текст рекламы нарисован");
    }

    [Fact]
    public void Cursor_pictures_are_giant_tiny_and_flipped()
    {
        var (size, scale) = GiantCursorHardship.Measure(32);
        Assert.Equal(128, size);
        var giant = CursorArt.Arrow(size, scale);
        var normal = CursorArt.Arrow(32, 1);
        Assert.True(CursorArt.Visible(giant.Pixels) >= CursorArt.Visible(normal.Pixels) * 30);

        var tiny = CursorArt.Tiny(32);
        Assert.InRange(CursorArt.Visible(tiny.Pixels), 5, 20);

        var flipped = CursorArt.Arrow(32, 1, flipped: true);
        Assert.Equal(CursorArt.Visible(normal.Pixels), CursorArt.Visible(flipped.Pixels));
        Assert.NotEqual(0u, flipped.Pixels[(flipped.HotY * 32) + flipped.HotX] >> 24);
        Assert.Equal((0, 0), (normal.HotX, normal.HotY));
    }

    [Fact]
    public void Hook_counts_the_physical_button_while_buttons_are_swapped()
    {
        Assert.Equal(MouseButton.Right, MouseHook.Translate(MouseHook.LeftDown, 0, TimeSpan.Zero, unswap: true)!.Button);
        Assert.Equal(MouseButton.Left, MouseHook.Translate(MouseHook.RightUp, 0, TimeSpan.Zero, unswap: true)!.Button);
        Assert.Equal(MouseButton.Middle, MouseHook.Translate(MouseHook.MiddleDown, 0, TimeSpan.Zero, unswap: true)!.Button);
        Assert.Equal(MouseButton.Left, MouseHook.Translate(MouseHook.LeftDown, 0, TimeSpan.Zero)!.Button);
    }

    private static double Distance(ScreenPoint a, ScreenPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}

/// <summary>
/// Усложнения этапа 15 в настоящей системе: курсор, кнопки, окна поверх экрана. Курсор и кнопки на доли секунды
/// меняются у всей системы, поэтому тесты идут вместе с тестами аварийной остановки, не параллельно.
/// Оверлеи открываются только на тестовом мониторе.
/// </summary>
[Collection(nameof(EmergencyStopTests))]
public sealed class HardshipCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly int _speed = CursorControl.Speed();
    private readonly bool _swapped = MouseButtons.IsSwapped();
    private readonly ScreenPoint _cursor = CursorControl.Position();

    public void Dispose()
    {
        // Если проверка упала посреди усложнения, человек не должен остаться без курсора и с чужими кнопками.
        CursorControl.RestoreCursors();
        if (CursorControl.Speed() != _speed)
        {
            CursorControl.SetSpeed(_speed);
        }

        if (MouseButtons.IsSwapped() != _swapped)
        {
            MouseButtons.SetSwapped(_swapped);
        }

        _temp.Dispose();
    }

    [Fact]
    public void Every_hardship_loads_from_the_modules_folder_starts_by_hand_and_is_fully_removed()
    {
        var modules = _temp.Combine("modules");
        foreach (var name in new[] { "ADHDWindows.Modules.Hardships", "ADHDWindows.Modules.Hardships.Basic", "ADHDWindows.Modules.Hardships.Cursor", "ADHDWindows.Modules.Hardships.Screen" })
        {
            RepositoryRoot.DeployModule(name, modules);
        }

        var bus = new EventBus(_log);
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        var store = new JsonFileStore(_temp.Combine("save.json"), _log);
        store.Load();
        store.Set($"modules/hardships/{HardshipsModule.IgnoreSafetySetting}", true);

        // «Одно окно» и «Бродячие окна» не трогают окна человека.
        store.Set($"modules/hardship.oneWindow/{OneWindowHardship.OnlyUnderSetting}", _temp.Combine("nothing"));
        store.Set($"modules/hardship.wanderingWindows/{WanderingWindowsHardship.OnlyUnderSetting}", _temp.Combine("nothing"));
        var economy = new CoinEconomy(store, bus, TimeProvider.System);
        var texts = new TextCatalog(data, "ru", _log);
        texts.Use("ru");
        HardshipStatus? status = null;
        var started = new List<string>();
        var ended = new List<HardshipEnded>();
        bus.Subscribe<HardshipStatus>(value => status = value);
        bus.Subscribe<HardshipStarted>(value =>
        {
            lock (started)
            {
                started.Add(value.Hardship.Id);
            }
        });
        bus.Subscribe<HardshipEnded>(value =>
        {
            lock (ended)
            {
                ended.Add(value);
            }
        });
        var host = new ModuleHost(bus, data, texts, economy, store, store, new RecoveryService(new ChangeJournal(_temp.Combine("recovery.json"), _log), () => [], _log), _log, developerMode: true);
        host.Start(ModuleLoader.Load(modules, _log));
        try
        {
            Assert.NotNull(status);
            var catalog = status.Hardships.Select(entry => entry.Hardship).ToList();
            Assert.True(catalog.Count >= 30, $"усложнений {catalog.Count}");
            Assert.Equal(catalog.Count, catalog.Select(info => info.Id).Distinct().Count());
            Assert.All(catalog, info =>
            {
                Assert.DoesNotContain("hardships.", texts.Get($"hardships.{info.Id}.title"));
                Assert.DoesNotContain("hardships.", texts.Get($"hardships.{info.Id}.text"));
            });
            foreach (var level in Enum.GetValues<HardshipLevel>())
            {
                Assert.True(catalog.Count(info => info.Level == level) >= 5, $"уровень {level}");
            }

            var restorers = ModuleLoader.LoadRestorers(modules, _log).Select(restorer => restorer.Kind).ToList();
            Assert.Contains(MouseButtonsRestorer.ChangeKind, restorers);
            Assert.Contains(WindowPositionsRestorer.ChangeKind, restorers);
            Assert.Equal(restorers.Count, restorers.Distinct().Count());

            var arrow = CursorControl.VisiblePixelsOfArrow();
            foreach (var info in catalog)
            {
                bus.Publish(new HardshipTestRequested(info.Id));
                WaitUntil(() => Contains(started, info.Id), $"запуск {info.Id}");
                Thread.Sleep(250);

                // Действие видно в системе.
                if (info.Id == "swapButtons")
                {
                    Assert.NotEqual(_swapped, MouseButtons.IsSwapped());
                }
                else if (info.Id is "snail" or "pollution")
                {
                    Assert.True(CursorControl.Speed() < _speed || _speed == CursorControl.MinSpeed, info.Id);
                }
                else if (info.Resources.HasFlag(HardshipResources.CursorLook) && info.Id is not "blink" and not "upsideDown")
                {
                    Assert.NotEqual(arrow, CursorControl.VisiblePixelsOfArrow());
                }
                else if (info.Resources == HardshipResources.Screen)
                {
                    Assert.True(OverlayWindowCount() > 0, $"окна оверлея {info.Id}");
                }

                bus.Publish(new HardshipCancelRequested());
                WaitUntil(() => Contains(ended, info.Id), $"снятие {info.Id}");

                // После снятия система такая же, как до запуска.
                Assert.Equal(arrow, CursorControl.VisiblePixelsOfArrow());
                Assert.Equal(_speed, CursorControl.Speed());
                Assert.Equal(_swapped, MouseButtons.IsSwapped());
                Assert.Equal(0, OverlayWindowCount());
            }

            Assert.Empty(_log.Errors);
        }
        finally
        {
            host.StopAll();
            CursorControl.MoveTo(_cursor);
        }

        bool Contains<T>(List<T> list, string id)
        {
            lock (list)
            {
                return list.Any(item => item is string text ? text == id : item is HardshipEnded end && end.Hardship.Id == id);
            }
        }
    }

    [Fact]
    public void Swatted_fly_pays_once_and_flies_back_later()
    {
        var fly = new FlyHardship();
        using var harness = new Harness(_temp, _log, fly);
        harness.Bus.Publish(new HardshipStartRequested("fly"));
        try
        {
            WaitUntil(() => fly.FlyBounds is not null, "муха");
            var before = harness.Economy.Balance;

            fly.SwatForTest();
            fly.SwatForTest();

            Assert.Equal(1, fly.Swatted);
            Assert.Equal(before + (long)FlyHardship.Bonus, harness.Economy.Balance);
            Assert.Null(fly.FlyBounds);
            WaitUntil(() => fly.FlyBounds is not null, "новая муха", TimeSpan.FromSeconds(10));
            Assert.True(fly.OverlayWindows > 0);
        }
        finally
        {
            harness.Bus.Publish(new HardshipStopRequested("fly"));
        }

        Assert.Equal(0, OverlayWindowCount());
    }

    [Fact]
    public void Ads_appear_close_with_the_cross_and_all_vanish_at_the_end()
    {
        var ads = new AdsHardship();
        using var harness = new Harness(_temp, _log, ads);
        harness.Bus.Publish(new HardshipStartRequested("ads"));
        try
        {
            Assert.Equal(1, ads.Open);
            ads.CloseOneForTest();
            Assert.Equal(0, ads.Open);
            Assert.Equal(1, ads.Closed);
            WaitUntil(() => ads.Open == 1, "следующая реклама", TimeSpan.FromSeconds(AdsHardship.MaxPause + 3));
        }
        finally
        {
            harness.Bus.Publish(new HardshipStopRequested("ads"));
        }

        Assert.Equal(0, OverlayWindowCount());
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Tax_takes_every_second_click_and_nothing_else()
    {
        var tax = new TaxHardship();
        using var harness = new Harness(_temp, _log, tax);

        void Click(MouseButton button, string source)
        {
            // Так модуль кликов сообщает о засчитанном клике перед начислением.
            harness.Bus.Publish(new ClickCounted(button, TimeSpan.Zero));
            harness.Economy.Earn(source, 1);
        }

        Click(MouseButton.Left, "click");
        Click(MouseButton.Left, "click");
        Assert.Equal(2, harness.Economy.Balance);

        harness.Bus.Publish(new HardshipStartRequested("tax"));
        for (var i = 0; i < 6; i++)
        {
            Click(MouseButton.Left, "click");
        }

        Assert.Equal(5, harness.Economy.Balance);
        Click(MouseButton.Right, "click.right");
        harness.Economy.Earn("time", 10);
        Assert.Equal(16, harness.Economy.Balance);

        harness.Bus.Publish(new HardshipStopRequested("tax"));
        Click(MouseButton.Left, "click");
        Click(MouseButton.Left, "click");
        Assert.Equal(18, harness.Economy.Balance);
    }

    [Fact]
    public void Physical_left_click_still_counts_as_left_while_buttons_are_swapped()
    {
        var swap = new SwapButtonsHardship();
        using var harness = new Harness(_temp, _log, swap);
        var clicks = new List<MouseButtonChanged>();
        using var hook = new MouseHook(
            change =>
            {
                lock (clicks)
                {
                    clicks.Add(change);
                }
            },
            _log);
        harness.Bus.Subscribe<MouseButtonsSwapped>(value => hook.Unswap = value.Swapped);
        Assert.True(hook.Start());

        // Клик приходится на окно-мишень на тестовом мониторе: так он не попадёт в окна человека.
        using var overlay = new OverlayThread("Target", _ => { }, _ => { });
        var area = TestScreen.Find()?.WorkArea ?? new ScreenRect(100, 100, 400, 400);
        var target = overlay.CreateSprite((_, _) => { });
        var spot = new ScreenPoint(area.Left + 60, area.Top + 60);
        overlay.Invoke(() => target.Show(Enumerable.Repeat(0xFF2B2647u, 16 * 16).ToArray(), 16, 16, 4, new ScreenPoint(spot.X - 32, spot.Y - 32)));
        try
        {
            harness.Bus.Publish(new HardshipStartRequested("swapButtons"));
            Assert.True(swap.IsActive);
            Assert.NotEqual(_swapped, MouseButtons.IsSwapped());
            CursorControl.MoveTo(spot);
            ClickLeft();
            WaitUntil(() => Count(clicks) >= 2, "клик");
            harness.Bus.Publish(new HardshipStopRequested("swapButtons"));
            Assert.Equal(_swapped, MouseButtons.IsSwapped());
            Assert.False(hook.Unswap);

            lock (clicks)
            {
                Assert.All(clicks, click => Assert.Equal(MouseButton.Left, click.Button));
            }
        }
        finally
        {
            CursorControl.MoveTo(_cursor);
        }

        static int Count(List<MouseButtonChanged> list)
        {
            lock (list)
            {
                return list.Count;
            }
        }
    }

    [Fact]
    public void Move_filter_hook_starts_and_stops_with_the_hardship()
    {
        var inversion = new InversionHardship();
        var lag = new LagHardship();
        using var harness = new Harness(_temp, _log, inversion, lag);

        harness.Bus.Publish(new HardshipStartRequested("inversion"));
        Assert.True(inversion.IsActive);
        harness.Bus.Publish(new HardshipStopRequested("inversion"));
        Assert.False(inversion.IsActive);

        harness.Bus.Publish(new HardshipStartRequested("lag"));
        Assert.True(lag.IsActive);
        harness.Bus.Publish(new HardshipStopRequested("lag"));
        Assert.False(lag.IsActive);
        Assert.Empty(_log.Errors);
        CursorControl.MoveTo(_cursor);
    }

    /// <summary>Окна оверлеев этого процесса, в том числе скрытые.</summary>
    internal static int OverlayWindowCount()
    {
        var count = 0;
        var process = Environment.ProcessId;
        EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                var name = new StringBuilder(128);
                if (owner == process && GetClassNameW(window, name, name.Capacity) > 0 && name.ToString().StartsWith("ADHDWindows.Overlay", StringComparison.Ordinal))
                {
                    count++;
                }

                return true;
            },
            IntPtr.Zero);
        return count;
    }

    private static void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(10)), $"Не дождались: {what}");
            Thread.Sleep(30);
        }
    }

    private static void ClickLeft()
    {
        const uint leftDown = 0x0002;
        const uint leftUp = 0x0004;
        mouse_event(leftDown, 0, 0, 0, IntPtr.Zero);
        mouse_event(leftUp, 0, 0, 0, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumCallback callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);

    private delegate bool EnumCallback(IntPtr window, IntPtr data);

    /// <summary>Ядро с указанными модулями без загрузки из папки: проверкам нужен доступ к внутренностям модуля.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly ModuleHost _host;

        public Harness(TempDirectory temp, RecordingLog log, params IModule[] modules)
        {
            Bus = new EventBus(log);
            var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), log);
            data.Load();
            var store = new JsonFileStore(temp.Combine($"save-{Guid.NewGuid():N}.json"), log);
            store.Load();
            Economy = new CoinEconomy(store, Bus, TimeProvider.System);
            var texts = new TextCatalog(data, "ru", log);
            texts.Use("ru");
            _host = new ModuleHost(Bus, data, texts, Economy, store, store, new RecoveryService(new ChangeJournal(temp.Combine($"recovery-{Guid.NewGuid():N}.json"), log), () => [], log), log, developerMode: true);
            _host.Start(modules);
        }

        public EventBus Bus { get; }

        public CoinEconomy Economy { get; }

        public void Dispose() => _host.StopAll();
    }
}
