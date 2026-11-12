using System.Diagnostics;
using System.Runtime.InteropServices;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Core.Input;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Modules.Hardships.Keyboard;
using ADHDWindows.Modules.Hardships.Physics;
using ADHDWindows.Modules.Hardships.Sounds;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

/// <summary>Логика усложнений этапа 16 без системы: снег, лопата, цепь, клоны, валуны, снайпер, шифр, клавиатура, звуки.</summary>
public sealed class UserHardshipPartsTests
{
    private const double Frame = 1 / 60.0;

    [Fact]
    public void Spread_stays_near_the_base_value_and_differs_between_runs()
    {
        var spread = new Spread(new Random(1));
        var values = Enumerable.Range(0, 200).Select(_ => spread.Vary(100.0)).ToList();
        Assert.All(values, value => Assert.InRange(value, 85, 115));
        Assert.True(values.Distinct().Count() > 150, "значения разные");
        var (min, max) = spread.Vary(1, 3);
        Assert.True(min < max);
        Assert.Equal(3, max / min, 6);
        Assert.True(spread.Vary(1) >= 1);
    }

    [Fact]
    public void Snow_covers_everything_and_is_cleared_only_where_dug()
    {
        var snow = new SnowField(new ScreenRect(100, 50, 800, 600), 8);
        Assert.True(snow.IsSnow(new Vec(500, 300)));
        Assert.False(snow.IsSnow(new Vec(50, 300)), "вне сугроба снега нет");
        Assert.Single(snow.Rects());

        var cleared = snow.ClearCircle(new Vec(500, 300), 120);
        Assert.InRange(cleared * 64, Math.PI * 120 * 120 * 0.85, Math.PI * 120 * 120 * 1.15);
        Assert.False(snow.IsSnow(new Vec(500, 300)));
        Assert.False(snow.IsSnow(new Vec(600, 300)));
        Assert.True(snow.IsSnow(new Vec(640, 300)));

        // Форма окна закрывает ровно клетки со снегом.
        var area = snow.Rects().Sum(rect => (long)rect.Width * rect.Height);
        Assert.Equal((long)snow.SnowCells * 64, area);
        Assert.True(snow.Rects().Count < snow.Rows * 3, "соседние полосы склеены");

        var version = snow.Version;
        Assert.Equal(0, snow.ClearCircle(new Vec(500, 300), 50));
        Assert.Equal(version, snow.Version);
    }

    [Fact]
    public void Cursor_stops_at_the_snow_wall_even_on_a_fast_move()
    {
        var snow = new SnowField(new ScreenRect(0, 0, 1000, 1000), 8);
        snow.ClearCircle(new Vec(500, 500), 100);
        snow.ClearCircle(new Vec(800, 500), 60);
        var stop = snow.Reach(new Vec(500, 500), new Vec(800, 500));
        Assert.InRange(stop.X, 580, 600);
        Assert.False(snow.IsSnow(stop));

        // Свободная дорога — без изменений.
        Assert.Equal(new Vec(550, 520), snow.Reach(new Vec(500, 500), new Vec(550, 520)));
    }

    [Fact]
    public void Held_shovel_follows_the_hand_turns_and_digs_with_its_blade()
    {
        var bounds = new ScreenRect(0, 0, 2000, 1200);
        var snow = new SnowField(bounds, 8);
        snow.ClearCircle(new Vec(500, 500), 150);
        var shovel = new Shovel(new Vec(500, 560), 0);
        var handle = new Vec(500 - 40, 560);
        Assert.True(shovel.HitsHandle(handle));
        Assert.False(shovel.HitsHandle(shovel.BladeCenter));
        Assert.True(shovel.HitsBlade(shovel.BladeCenter));

        shovel.Grab(handle);
        Assert.True(shovel.IsHeld);
        var before = snow.SnowCells;
        var hand = handle;
        for (var i = 0; i < 120; i++)
        {
            // Рука уходит вверх: лопата тянется за ней и поворачивается штыком вниз.
            hand = new Vec(460, 560 - (i * 3));
            shovel.Step(Frame, hand, snow, bounds);
        }

        Assert.True((shovel.Position - hand).Length < 120, "лопата рядом с рукой");
        Assert.True(Math.Abs(Math.Sin(shovel.Angle)) > 0.7, $"лопата повернулась: {shovel.Angle}");
        Assert.True(snow.SnowCells < before, "штык расчистил снег");
    }

    [Fact]
    public void Thrown_shovel_flies_on_and_slows_down()
    {
        var bounds = new ScreenRect(0, 0, 3000, 2000);
        var shovel = new Shovel(new Vec(500, 500), 0);
        shovel.Grab(new Vec(470, 500));
        for (var i = 0; i < 30; i++)
        {
            shovel.Step(Frame, new Vec(470 + (i * 20), 500), null, bounds);
        }

        shovel.Release();
        var speed = shovel.Velocity.Length;
        Assert.True(speed > 300, $"лопата брошена: {speed}");
        var start = shovel.Position;
        for (var i = 0; i < 60; i++)
        {
            shovel.Step(Frame, Vec.Zero, null, bounds);
        }

        Assert.True((shovel.Position - start).Length > 100, "летит по инерции");
        Assert.True(shovel.Velocity.Length < speed, "замедляется");

        // В снегу лопата вязнет сильнее.
        var free = new Shovel(new Vec(500, 500), 0);
        var stuck = new Shovel(new Vec(500, 500), 0);
        var snow = new SnowField(bounds, 8);
        foreach (var item in new[] { free, stuck })
        {
            item.Grab(new Vec(470, 500));
            for (var i = 0; i < 30; i++)
            {
                item.Step(Frame, new Vec(470 + (i * 20), 500), null, bounds);
            }

            item.Release();
        }

        for (var i = 0; i < 20; i++)
        {
            free.Step(Frame, Vec.Zero, null, bounds);
            stuck.Step(Frame, Vec.Zero, snow, bounds);
        }

        Assert.True(stuck.Velocity.Length < free.Velocity.Length * 0.8);
    }

    [Fact]
    public void Shovel_picture_is_drawn_around_its_position()
    {
        var shovel = new Shovel(new Vec(300, 300), 0.7);
        var (picture, origin) = ShovelArt.Render(shovel, 2);
        Assert.True(picture.Pixels.Count(pixel => pixel != 0) > 100);
        Assert.InRange(300 - origin.X, picture.Width - 2, picture.Width + 2);
    }

    [Fact]
    public void Chain_bot_drags_the_cursor_but_a_sharp_jerk_wins()
    {
        var bounds = new ScreenRect(0, 0, 4000, 4000);
        var settings = new ChainSettings(WanderSpeed: 0, MinDashPause: 1000, MaxDashPause: 1000);
        var bot = new ChainBot(new Random(1), settings, new Vec(2000, 2000));

        // Человек медленно уходит: цепь тянет его назад сильнее, чем сдвигается бот.
        var user = new Vec(2150, 2000);
        var pulledBack = 0.0;
        for (var i = 0; i < 120; i++)
        {
            user += new Vec(2, 0);
            var shift = bot.Step(Frame, user, new Vec(120, 0), held: false, bounds);
            pulledBack += -shift.X;
            user += shift;
            Assert.True((bot.Position - user).Length <= settings.Length + 1, "цепь не длиннее своей длины");
        }

        Assert.True(pulledBack > 60, $"бот тянет: {pulledBack}");

        // Резкий рывок: бот получает скорость и летит за человеком, человека почти не тянет назад.
        var jerkBot = new ChainBot(new Random(1), settings, new Vec(2000, 2000));
        var jerkUser = new Vec(2200, 2000);
        var shiftOnJerk = jerkBot.Step(Frame, jerkUser + new Vec(40, 0), new Vec(2400, 0), held: false, bounds);
        Assert.True(Math.Abs(shiftOnJerk.X) < 5, $"рывок побеждает: {shiftOnJerk.X}");
        Assert.True(jerkBot.Velocity.X > 300, "бот полетел за рукой");

        // Пока кнопка нажата, бот не тянет.
        var heldBot = new ChainBot(new Random(1), settings, new Vec(2000, 2000));
        Assert.Equal(Vec.Zero, heldBot.Step(Frame, new Vec(2400, 2000), Vec.Zero, held: true, bounds));
        Assert.True((heldBot.Position - new Vec(2400, 2000)).Length <= settings.Length + 0.01);
    }

    [Fact]
    public void Chain_bot_dashes_every_few_seconds()
    {
        var bot = new ChainBot(new Random(2), new ChainSettings(), new Vec(1000, 1000));
        var user = new Vec(1000, 1000);
        var dragged = false;
        for (var i = 0; i < 60 * 30; i++)
        {
            var shift = bot.Step(Frame, user, Vec.Zero, held: false, new ScreenRect(0, 0, 2000, 2000));
            dragged |= shift.Length > 0.5;
            user += shift;
        }

        Assert.InRange(bot.Dashes, 30 / 8, (30 / 4) + 1);
        Assert.True(dragged, "рывок дёрнул курсор");
    }

    [Fact]
    public void Chain_sags_when_slack_and_is_straight_when_tight()
    {
        var tight = ChainLinks.Points(new Vec(0, 0), new Vec(200, 0), 200, 10);
        Assert.All(tight, point => Assert.InRange(point.Y, -0.01, 0.01));
        var slack = ChainLinks.Points(new Vec(0, 0), new Vec(100, 0), 200, 10);
        Assert.True(slack.Max(point => point.Y) > 30, "свободная цепь провисает");
    }

    [Fact]
    public void Clones_walk_with_pauses_inside_their_screen()
    {
        var screen = new ScreenRect(100, 100, 800, 600);
        var clone = new CloneWalker(new Random(3), screen);
        var positions = new List<Vec>();
        for (var i = 0; i < 60 * 20; i++)
        {
            clone.Step(Frame);
            positions.Add(clone.Position);
            Assert.InRange(clone.Position.X, screen.Left - 1, screen.Right);
            Assert.InRange(clone.Position.Y, screen.Top - 1, screen.Bottom);
        }

        var moving = positions.Zip(positions.Skip(1)).Count(pair => (pair.First - pair.Second).Length > 0.01);
        Assert.InRange(moving, 60, (60 * 20) - 60);
    }

    [Fact]
    public void Boulder_falls_faster_and_knocks_the_cursor_harder_when_bigger()
    {
        var settings = new RockSettings();
        var rock = new Boulder(new Vec(500, 0), 40, 0);
        rock.Step(0.1, settings);
        var first = rock.Velocity.Y;
        rock.Step(0.1, settings);
        Assert.True(rock.Velocity.Y > first, "падает с ускорением");
        Assert.Null(rock.Push(new Vec(800, 0), settings));

        var small = new Boulder(new Vec(500, 500), 30, 0);
        var big = new Boulder(new Vec(500, 500), 80, 0);
        for (var i = 0; i < 20; i++)
        {
            small.Step(Frame, settings);
            big.Step(Frame, settings);
        }

        var smallPush = small.Push(small.Position + new Vec(2, 5), settings)!.Value;
        var bigPush = big.Push(big.Position + new Vec(2, 5), settings)!.Value;
        Assert.True((smallPush.Point - small.Position).Length >= small.Radius, "курсор вытолкнут за край");
        Assert.True(smallPush.Knock.Y > 0, "толкает вниз");
        Assert.True(bigPush.Knock.Y > smallPush.Knock.Y * 1.5, "крупный толкает сильнее");

        var knock = new Knock();
        knock.Add(bigPush.Knock);
        var moved = 0;
        for (var i = 0; i < 60; i++)
        {
            moved += knock.Step(Frame).Y;
        }

        Assert.True(moved > 10);
        Assert.Equal(Vec.Zero, knock.Velocity);
    }

    [Fact]
    public void Rock_pictures_have_four_turns_of_the_right_size()
    {
        var frames = RockArt.Frames(60, new Random(4));
        Assert.Equal(4, frames.Length);
        Assert.All(frames, frame =>
        {
            Assert.Equal(20, frame.Width);
            Assert.True(frame.Pixels.Count(pixel => pixel != 0) > 150);
        });
    }

    [Fact]
    public void Sniper_follows_the_trail_aims_and_shoots_a_resting_cursor()
    {
        var sniper = new SniperSight(new SniperSettings());
        var time = 0.0;

        // Курсор едет вправо: прицел повторяет путь с запаздыванием.
        for (var i = 0; i < 120; i++)
        {
            time += Frame;
            Assert.False(sniper.Step(time, new Vec(100 + (i * 5), 300)));
        }

        Assert.InRange(sniper.Sight.X, 100 + ((120 - 56) * 5) - 10, 100 + ((120 - 52) * 5) + 10);
        Assert.Equal(SniperState.Tracking, sniper.State);

        // Курсор стоит: прицел догоняет, краснеет, через 2 секунды выстрел.
        var shotAt = -1.0;
        for (var i = 0; i < 60 * 4 && shotAt < 0; i++)
        {
            time += Frame;
            if (sniper.Step(time, new Vec(700, 300)))
            {
                shotAt = time;
            }
        }

        Assert.True(shotAt > 0, "выстрел был");
        Assert.InRange(shotAt - (120 * Frame), 0.9 + 2 - 0.1, 0.9 + 2 + 0.2);
        Assert.True(sniper.IsFrozen(shotAt + 4.9));
        Assert.False(sniper.IsFrozen(shotAt + 5.1));
        Assert.False(sniper.SightVisible);
        Assert.Equal(1, sniper.Shots);

        while (time < shotAt + 6.9)
        {
            time += Frame;
            sniper.Step(time, new Vec(700, 300));
            Assert.False(sniper.SightVisible, "прицел пропал на 7 секунд");
        }

        time += 0.2;
        sniper.Step(time, new Vec(700, 300));
        Assert.True(sniper.SightVisible);
    }

    [Fact]
    public void Sniper_countdown_resets_when_the_cursor_slips_away()
    {
        var sniper = new SniperSight(new SniperSettings());
        var time = 0.0;
        for (var i = 0; i < 60; i++)
        {
            time += Frame;
            sniper.Step(time, new Vec(300, 300));
        }

        Assert.Equal(SniperState.Aiming, sniper.State);
        Assert.InRange(sniper.AimProgress, 0.4, 0.7);

        for (var i = 0; i < 10; i++)
        {
            time += Frame;
            Assert.False(sniper.Step(time, new Vec(300 + (i * 10), 300)));
        }

        Assert.Equal(SniperState.Tracking, sniper.State);
        Assert.Equal(0, sniper.AimProgress);
    }

    [Fact]
    public void Caesar_shifts_letters_to_the_right_neighbour_and_wraps_the_row()
    {
        var cipher = new CaesarCipher(KeyboardData.Default.Caesar);
        Assert.Equal('е', cipher.Shift('к'));
        Assert.Equal('Е', cipher.Shift('К'));
        Assert.Equal('й', cipher.Shift('ъ'));
        Assert.Equal('Й', cipher.Shift('Ъ'));
        Assert.Equal('ф', cipher.Shift('э'));
        Assert.Equal('я', cipher.Shift('ю'));
        Assert.Equal('w', cipher.Shift('q'));
        Assert.Equal('q', cipher.Shift('p'));
        Assert.Equal('A', cipher.Shift('L'));
        Assert.Equal('z', cipher.Shift('m'));
        Assert.Equal('5', cipher.Shift('5'));
        Assert.Equal('ё', cipher.Shift('ё'));
        Assert.Equal('.', cipher.Shift('.'));

        // Клавиша R (scan 0x13): в русской раскладке К, печатается Е.
        Assert.Equal('к', cipher.Letter(0x13, 0x0419));
        Assert.Equal('r', cipher.Letter(0x13, 0x0409));
        Assert.Equal('Е', cipher.Replace(0x13, 0x0419, upper: true));
        Assert.Equal('t', cipher.Replace(0x13, 0x0409, upper: false));
        Assert.Null(cipher.Letter(0x35, 0x0419));
        Assert.Null(cipher.Letter(0x13, 0x0407));
        Assert.Null(cipher.Letter(0x02, 0x0409));
    }

    [Fact]
    public void Caesar_hook_swallows_letters_but_not_shortcuts_or_protected_places()
    {
        using var temp = new TempDirectory();
        var log = new RecordingLog();
        var caesar = new CaesarHardship();
        using var harness = new HardshipHarness(temp, log, caesar);
        var typed = new List<char>();
        caesar.Output = typed.Add;
        harness.Bus.Publish(new HardshipStartRequested("caesar"));
        try
        {
            Assert.True(caesar.IsActive);
            var protect = true;
            caesar.Guard!.Override = () => protect;
            protect = false;
            WaitUntil(() => !caesar.Guard.IsProtected, "проверка защиты");

            var r = new KeyStroke(0x52, 0x13, true, false);
            Assert.True(caesar.Decide(r, 0x0419, upper: false, shortcut: false));
            Assert.True(caesar.Decide(r with { Down = false }, 0x0419, false, false), "отпускание тоже проглочено");
            Assert.False(caesar.Decide(r, 0x0419, upper: false, shortcut: true), "Ctrl+R работает");
            Assert.False(caesar.Decide(r with { Down = false }, 0x0419, false, false));
            Assert.False(caesar.Decide(new KeyStroke(0x31, 0x02, true, false), 0x0419, false, false), "цифры не меняются");
            Assert.False(caesar.Decide(new KeyStroke(0x51, 0x10, true, false), 0x0407, false, false), "другие раскладки не меняются");
            Assert.Equal(['е'], typed);

            protect = true;
            WaitUntil(() => caesar.Guard.IsProtected, "защищённое место");
            Assert.False(caesar.Decide(r, 0x0419, upper: false, shortcut: false), "в защищённом месте обычная клавиатура");
            Assert.Equal(1, caesar.Replaced);
        }
        finally
        {
            harness.Bus.Publish(new HardshipStopRequested("caesar"));
        }

        Assert.False(caesar.IsActive);
        Assert.Null(caesar.Guard);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Keyboard_guard_protects_terminals_claude_and_this_program()
    {
        using var guard = new KeyboardGuard(KeyboardData.Default.Protected, new RecordingLog());
        Assert.True(guard.IsProtectedFile(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe"));
        Assert.True(guard.IsProtectedFile(@"C:\Users\me\AppData\Local\AnthropicClaude\claude.exe"));
        Assert.True(guard.IsProtectedFile(@"C:\Windows\System32\cmd.exe"));
        Assert.False(guard.IsProtectedFile(@"C:\Windows\System32\notepad.exe"));
        Assert.True(guard.IsProtectedClass("ConsoleWindowClass"));
        Assert.False(guard.IsProtectedClass("Notepad"));
        Assert.True(guard.IsProtectedProcess(Environment.ProcessId));
    }

    [Fact]
    public void Keyboard_data_file_matches_the_built_in_rows()
    {
        var log = new RecordingLog();
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), log);
        data.Load();
        var keyboard = KeyboardData.Load(data);
        Assert.NotSame(KeyboardData.Default, keyboard);
        Assert.Contains("claude", keyboard.Protected.Processes);
        Assert.All(keyboard.Layouts.Values, layout =>
        {
            Assert.Equal(layout.Rows.Count, layout.Shifted.Count);
            for (var i = 0; i < layout.Rows.Count; i++)
            {
                Assert.Equal(layout.Rows[i].Length, layout.Shifted[i].Length);
            }
        });
        Assert.Equal(KeyboardData.Default.Caesar["ru"], keyboard.Caesar["ru"]);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Virtual_keyboard_types_with_one_shot_shift_and_switches_layouts()
    {
        var keyboard = new VirtualKeyboard([.. KeyboardData.Default.Layouts.Values]);
        var keys = keyboard.Keys;
        Assert.All(keys, key => Assert.True(new ScreenRect(0, 0, keyboard.Width, keyboard.Height).Contains(key.Bounds), $"{key} внутри"));
        foreach (var key in keys)
        {
            Assert.DoesNotContain(keys, other => other != key && other.Bounds.Overlap(key.Bounds) > 0);
        }

        VirtualKey Find(string label) => keys.First(key => keyboard.Label(key) == label);
        Assert.Equal('й', keyboard.Press(Find("й"))!.Character);
        Assert.Null(keyboard.Press(keys.First(key => key.Kind == VirtualKeyKind.Shift)));
        Assert.Equal('Ц', keyboard.Press(Find("Ц"))!.Character);
        Assert.Equal('у', keyboard.Press(Find("у"))!.Character);
        Assert.Equal(KeyboardInput.Backspace, keyboard.Press(keys.First(key => key.Kind == VirtualKeyKind.Backspace))!.Press);
        Assert.Equal(KeyboardInput.Enter, keyboard.Press(keys.First(key => key.Kind == VirtualKeyKind.Enter))!.Press);
        Assert.Equal(' ', keyboard.Press(keys.First(key => key.Kind == VirtualKeyKind.Space))!.Character);

        var language = keys.First(key => key.Kind == VirtualKeyKind.Language);
        Assert.Equal("EN", keyboard.Label(language));
        keyboard.Press(language);
        Assert.Equal("EN", keyboard.Layout.Name);
        Assert.Equal('q', keyboard.Press(Find("q"))!.Character);
        Assert.Equal(keys.First(key => key.Kind == VirtualKeyKind.Shift), keyboard.KeyAt(keys.First(key => key.Kind == VirtualKeyKind.Shift).Bounds.Left + 1, keys.First(key => key.Kind == VirtualKeyKind.Shift).Bounds.Top + 1));
        Assert.True(keyboard.InTitle(3, 3));
        Assert.Null(keyboard.KeyAt(1, 1));
    }

    [Fact]
    public void Only_typing_keys_are_taken_from_the_real_keyboard()
    {
        foreach (var key in new[] { 0x41, 0x5A, 0x30, 0x39, 0x20, 0x0D, 0x08, 0xBA, 0xDE, 0x60, 0x6B })
        {
            Assert.True(VirtualKeyboardHardship.Types(key), $"0x{key:X}");
        }

        // Стрелки, F-клавиши, Esc, Tab, Delete, модификаторы работают.
        foreach (var key in new[] { 0x25, 0x26, 0x27, 0x28, 0x70, 0x74, 0x1B, 0x09, 0x2E, 0x10, 0x11, 0x12, 0x5B, 0x14 })
        {
            Assert.False(VirtualKeyboardHardship.Types(key), $"0x{key:X}");
        }
    }

    [Fact]
    public void Random_sounds_use_windows_and_own_files_and_keep_quiet()
    {
        using var temp = new TempDirectory();
        var log = new RecordingLog();
        var sounds = new RandomSoundsHardship();
        var played = new List<string>();
        sounds.Play = path =>
        {
            played.Add(path);
            return true;
        };
        using var harness = new HardshipHarness(temp, log, sounds);
        harness.Bus.Publish(new HardshipStartRequested("randomSounds"));
        try
        {
            Assert.True(sounds.IsActive);
            Assert.Contains(sounds.Files, path => path.EndsWith("knock.wav", StringComparison.Ordinal));
            Assert.Contains(sounds.Files, path => path.EndsWith("drip.wav", StringComparison.Ordinal));
            Assert.Contains(sounds.Files, path => path.StartsWith(Platform.Sounds.SystemFolder, StringComparison.OrdinalIgnoreCase));
            Assert.All(sounds.Files, path => Assert.True(File.Exists(path)));

            sounds.PlayNowForTest();
            sounds.PlayNowForTest();
            Assert.Equal(2, played.Count);
            Assert.NotEqual(played[0], played[1]);

            // «Не беспокоить»: звуки пропускаются.
            harness.Bus.Publish(new HardshipStatus(HardshipMode.Normal, 1, [], null, null, null, DateTimeOffset.UtcNow.AddMinutes(10), "quiet"));
            sounds.PlayNowForTest();
            Assert.Equal(2, played.Count);
            Assert.Equal(1, sounds.Skipped);

            harness.Bus.Publish(new HardshipStatus(HardshipMode.Normal, 1, [], null, null, null, null, null));
            sounds.PlayNowForTest();
            Assert.Equal(3, played.Count);
        }
        finally
        {
            harness.Bus.Publish(new HardshipStopRequested("randomSounds"));
        }

        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Own_sounds_are_short_8_bit_wav_files()
    {
        var folder = Path.Combine(RepositoryRoot.Find(), "assets", "sounds");
        var files = Directory.GetFiles(folder, "*.wav", SearchOption.AllDirectories);

        // Выстрел и три случайных звука, звуки интерфейса и мелодия (этап 19).
        Assert.Equal(4, files.Count(file => Path.GetFileName(Path.GetDirectoryName(file)) is "sounds" or "random"));
        Assert.True(files.Length >= 16);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal(1, BitConverter.ToInt16(bytes, 22));
            Assert.Equal(8, BitConverter.ToInt16(bytes, 34));
            var seconds = BitConverter.ToInt32(bytes, 40) / (double)BitConverter.ToInt32(bytes, 24);
            var music = Path.GetFileName(Path.GetDirectoryName(file)) == "music";
            Assert.InRange(seconds, music ? 5 : 0.1, music ? 30 : 3);
        }
    }

    internal static void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(10)), $"Не дождались: {what}");
            Thread.Sleep(20);
        }
    }
}

/// <summary>
/// Усложнения этапа 16 в работе: окна оверлеев на тестовом мониторе, настоящие хуки мыши и клавиатуры.
/// Тесты двигают системный курсор, поэтому идут не параллельно с другими такими же.
/// </summary>
[Collection(nameof(EmergencyStopTests))]
public sealed class UserHardshipLiveTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly ScreenPoint _cursor = CursorControl.Position();

    public void Dispose()
    {
        CursorControl.MoveTo(_cursor);
        _temp.Dispose();
    }

    [Fact]
    public void Clicks_into_snow_reach_neither_programs_nor_coins_and_the_cursor_stays_out()
    {
        var area = TestScreen.Find()?.Bounds ?? new ScreenRect(0, 0, 1280, 720);
        var center = new ScreenPoint(area.Left + (area.Width / 2), area.Top + (area.Height / 2));
        CursorControl.MoveTo(center);
        var snowdrift = new SnowdriftHardship();
        using var harness = new HardshipHarness(_temp, _log, snowdrift);
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
        Assert.True(hook.Start());

        // Окно-мишень под снегом и в свободном круге.
        using var overlay = new OverlayThread("Targets", _ => { }, _ => { });
        var hits = 0;
        var snowSpot = new ScreenPoint(area.Left + 60, area.Top + 60);
        var freeSpot = new ScreenPoint(center.X + 30, center.Y - 30);
        foreach (var spot in new[] { snowSpot, freeSpot })
        {
            var target = overlay.CreateSprite((_, _) => Interlocked.Increment(ref hits));
            overlay.Invoke(() => target.Show(Enumerable.Repeat(0xFF2B2647u, 8 * 8).ToArray(), 8, 8, 4, new ScreenPoint(spot.X - 16, spot.Y - 16)));
        }

        harness.Bus.Publish(new HardshipStartRequested("snowdrift"));
        try
        {
            Assert.True(snowdrift.IsActive);
            UserHardshipPartsTests.WaitUntil(() => HardshipCatalogTests.OverlayWindowCount() >= 4, "снег и лопата");
            var snow = snowdrift.Snow!;
            Assert.True(snow.IsSnow(snowSpot));
            Assert.False(snow.IsSnow(freeSpot));

            // Курсор упирается в снег.
            var stop = snowdrift.Confine(center, snowSpot);
            Assert.NotNull(stop);
            Assert.False(snow.IsSnow(stop.Value));
            Assert.Null(snowdrift.Confine(center, freeSpot));

            // Клик в снег: ни мишень, ни подсчёт кликов его не видят.
            CursorControl.MoveTo(snowSpot);
            ClickLeft();
            Thread.Sleep(300);
            Assert.Equal(0, Volatile.Read(ref hits));
            lock (clicks)
            {
                Assert.True(clicks.Count == 0, string.Join("; ", clicks));
            }

            Assert.Equal(1, snowdrift.BlockedClicks);

            // Клик в свободном круге проходит.
            CursorControl.MoveTo(freeSpot);
            ClickLeft();
            UserHardshipPartsTests.WaitUntil(() => Count(clicks) >= 2, "клик в свободном месте");
            UserHardshipPartsTests.WaitUntil(() => Volatile.Read(ref hits) == 1, "мишень в свободном месте");

            // Нажатие на ручку берёт лопату, отпускание бросает; оба события проглочены.
            var shovel = snowdrift.Shovel!;
            var handle = shovel.Position + new Vec(-shovel.Length * 0.3, 0).Rotate(shovel.Angle);
            Assert.True(snowdrift.OnButton(0x0201, handle.ToPoint()), "нажатие на ручку взяло лопату");
            Assert.True(shovel.IsHeld);
            Assert.True(snowdrift.OnButton(0x0202, handle.ToPoint()), "отпускание бросило лопату");
            Assert.False(shovel.IsHeld);

            // Отпускание без проглоченного нажатия проходит: кнопка не залипает.
            Assert.False(snowdrift.OnButton(0x0201, freeSpot));
            Assert.False(snowdrift.OnButton(0x0202, freeSpot));

            // Лопату ведут курсором и бросают: она расчищает снег. Настоящие кнопки человека здесь не трогаются.
            shovel.Grab(handle);
            var before = snow.SnowCells;
            for (var i = 0; i < 25; i++)
            {
                CursorControl.MoveTo(new ScreenPoint(handle.ToPoint().X + (i * 4), handle.ToPoint().Y - (i * 6)));
                Thread.Sleep(16);
            }

            shovel.Release();
            UserHardshipPartsTests.WaitUntil(() => snow.SnowCells < before, "лопата расчистила снег");
        }
        finally
        {
            harness.Bus.Publish(new HardshipStopRequested("snowdrift"));
        }

        Assert.False(snowdrift.IsActive);
        Assert.Equal(2, HardshipCatalogTests.OverlayWindowCount());

        // После снятия клики снова проходят везде.
        CursorControl.MoveTo(snowSpot);
        ClickLeft();
        UserHardshipPartsTests.WaitUntil(() => Volatile.Read(ref hits) == 2, "клик после снятия");
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Chain_clones_rocks_and_sniper_draw_on_the_test_screen_and_vanish()
    {
        var area = TestScreen.Find()?.Bounds ?? new ScreenRect(0, 0, 1280, 720);
        CursorControl.MoveTo(new ScreenPoint(area.Left + (area.Width / 2), area.Top + (area.Height / 2)));
        var chain = new ChainHardship();
        var clones = new ClonesHardship();
        var rocks = new RockfallHardship { Scope = _ => false };
        var sniper = new SniperHardship { Play = _ => true };
        using var harness = new HardshipHarness(_temp, _log, chain, clones, rocks, sniper);

        harness.Bus.Publish(new HardshipStartRequested("chain"));
        UserHardshipPartsTests.WaitUntil(() => HardshipCatalogTests.OverlayWindowCount() == 2, "бот и цепь");
        Assert.True(area.Contains(chain.Bot!.Position.ToPoint()));
        harness.Bus.Publish(new HardshipStopRequested("chain"));
        Assert.Equal(0, HardshipCatalogTests.OverlayWindowCount());

        harness.Bus.Publish(new HardshipStartRequested("clones"));
        Assert.InRange(clones.Count, ClonesHardship.MinPerScreen, ClonesHardship.MaxPerScreen * Math.Max(1, PhysicsArea.Screens().Count));
        harness.Bus.Publish(new HardshipStopRequested("clones"));
        Assert.Equal(0, HardshipCatalogTests.OverlayWindowCount());

        harness.Bus.Publish(new HardshipStartRequested("rockfall"));
        try
        {
            // Валун падает прямо на курсор и сталкивает его вниз.
            var spot = new ScreenPoint(area.Left + (area.Width / 2), area.Top + 200);
            CursorControl.MoveTo(spot);
            rocks.DropForTest(spot, 70);
            UserHardshipPartsTests.WaitUntil(() => rocks.CursorHits > 0, "попадание в курсор");
            UserHardshipPartsTests.WaitUntil(() => CursorControl.Position().Y > spot.Y + 30, "курсор сдвинут вниз");
            Assert.True(rocks.Falling >= 1);
        }
        finally
        {
            harness.Bus.Publish(new HardshipStopRequested("rockfall"));
        }

        Assert.Equal(0, HardshipCatalogTests.OverlayWindowCount());
        Assert.Empty(rocks.Moved);

        harness.Bus.Publish(new HardshipStartRequested("sniper"));
        try
        {
            var spot = new ScreenPoint(area.Left + 300, area.Top + 300);
            CursorControl.MoveTo(spot);
            UserHardshipPartsTests.WaitUntil(() => sniper.Sniper!.Shots == 1, "выстрел", TimeSpan.FromSeconds(8));

            // После выстрела курсор обездвижен.
            Assert.Equal(spot, sniper.Freeze(spot, new ScreenPoint(spot.X + 50, spot.Y)));
        }
        finally
        {
            harness.Bus.Publish(new HardshipStopRequested("sniper"));
        }

        Assert.Equal(0, HardshipCatalogTests.OverlayWindowCount());
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Rocks_push_a_window_down_by_its_title_and_put_it_back()
    {
        var area = TestScreen.Find()?.WorkArea ?? new ScreenRect(0, 0, 1280, 720);

        // Окно тестовой программы заменяет обычное окно с заголовком: ADHDWindows.TestApp открывается на тестовом мониторе.
        using var app = TestWindow.Start(_temp);
        var window = app.Window;
        var origin = WindowTraits.Bounds(window);
        var rocks = new RockfallHardship();
        using var harness = new HardshipHarness(_temp, _log, rocks);
        rocks.Scope = info => info.Handle == window;
        harness.Bus.Publish(new HardshipStartRequested("rockfall"));
        try
        {
            CursorControl.MoveTo(new ScreenPoint(area.Left + 5, area.Bottom - 5));
            rocks.DropForTest(new ScreenPoint(origin.Left + (origin.Width / 2), origin.Top + 8), 60);
            UserHardshipPartsTests.WaitUntil(() => WindowTraits.Bounds(window).Top > origin.Top + 10, "окно сдвинуто вниз");
            Assert.Single(rocks.Moved);
            Assert.Equal(origin.Top, rocks.Moved[0].Y);
        }
        finally
        {
            harness.Bus.Publish(new HardshipStopRequested("rockfall"));
        }

        UserHardshipPartsTests.WaitUntil(() => WindowTraits.Bounds(window) == origin, "окно на месте");
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Virtual_keyboard_types_with_the_mouse_moves_and_frees_the_real_keyboard_at_the_end()
    {
        var keyboardHardship = new VirtualKeyboardHardship();
        using var harness = new HardshipHarness(_temp, _log, keyboardHardship);
        var typed = new List<VirtualPress>();
        keyboardHardship.Output = typed.Add;
        harness.Bus.Publish(new HardshipStartRequested("virtualKeyboard"));
        try
        {
            Assert.True(keyboardHardship.IsActive);
            var bounds = keyboardHardship.Bounds!.Value;
            var area = TestScreen.Find()?.WorkArea;
            if (area is { } screen)
            {
                Assert.True(screen.Contains(bounds), "клавиатура внизу тестового монитора");
                Assert.True(bounds.Bottom > screen.Bottom - 40);
            }

            var keyboard = keyboardHardship.Keyboard!;
            keyboardHardship.ClickForTest(keyboard.Keys.First(key => keyboard.Label(key) == "к"));
            keyboardHardship.ClickForTest(keyboard.Keys.First(key => key.Kind == VirtualKeyKind.Backspace));
            Assert.Equal([new VirtualPress('к', null), new VirtualPress(null, KeyboardInput.Backspace)], typed);

            keyboardHardship.DragForTest(-90, -60);
            var moved = keyboardHardship.Bounds!.Value;
            Assert.InRange(moved.Left - bounds.Left, -93, -87);
            Assert.InRange(moved.Top - bounds.Top, -63, -57);

            var protect = false;
            keyboardHardship.Guard!.Override = () => protect;
            UserHardshipPartsTests.WaitUntil(() => !keyboardHardship.Guard.IsProtected, "проверка защиты");
            var a = new KeyStroke(0x41, 0x1E, true, false);
            Assert.True(keyboardHardship.Decide(a, shortcut: false), "буква исчезает");
            Assert.True(keyboardHardship.Decide(a with { Down = false }, shortcut: false));
            Assert.False(keyboardHardship.Decide(a, shortcut: true), "Ctrl+A работает");
            Assert.False(keyboardHardship.Decide(a with { Down = false }, shortcut: false));
            Assert.False(keyboardHardship.Decide(new KeyStroke(0x25, 0x4B, true, true), shortcut: false), "стрелки работают");
            protect = true;
            UserHardshipPartsTests.WaitUntil(() => keyboardHardship.Guard.IsProtected, "защищённое место");
            Assert.False(keyboardHardship.Decide(a, shortcut: false), "в защищённом месте клавиатура обычная");
            Assert.Equal(1, keyboardHardship.Blocked);
        }
        finally
        {
            harness.Bus.Publish(new HardshipStopRequested("virtualKeyboard"));
        }

        Assert.False(keyboardHardship.IsActive);
        Assert.Null(keyboardHardship.Guard);
        Assert.Equal(0, HardshipCatalogTests.OverlayWindowCount());
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Arrow_image_is_read_from_the_system()
    {
        var image = CursorControl.ArrowImage();
        Assert.NotNull(image);
        Assert.True(image.Width >= 16 && image.Height >= 16);
        Assert.True(image.Pixels.Count(pixel => (pixel >> 24) != 0) > 20);
        Assert.InRange(image.HotX, 0, image.Width - 1);
    }

    private static int Count(List<MouseButtonChanged> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    private static void ClickLeft()
    {
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
}

/// <summary>Окно тестовой программы ADHDWindows.TestApp на тестовом мониторе.</summary>
internal sealed class TestWindow : IDisposable
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private readonly Process _process;

    private TestWindow(Process process, IntPtr window)
    {
        _process = process;
        Window = window;
    }

    public IntPtr Window { get; }

    public static TestWindow Start(TempDirectory temp)
    {
        var output = Path.Combine(RepositoryRoot.Find(), "tests", "ADHDWindows.TestApp", "bin", Configuration, "net10.0-windows");
        var info = new ProcessStartInfo(Path.Combine(output, "ADHDWindows.TestApp.exe")) { UseShellExecute = false };
        info.ArgumentList.Add(temp.Combine($"beat-{Guid.NewGuid():N}.txt"));
        var process = Process.Start(info)!;
        var window = IntPtr.Zero;
        UserHardshipPartsTests.WaitUntil(
            () =>
            {
                window = WindowControl.AppWindows().FirstOrDefault(item => item.ProcessId == process.Id)?.Handle ?? IntPtr.Zero;
                return window != IntPtr.Zero;
            },
            "окно тестовой программы");
        return new TestWindow(process, window);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill();
            _process.WaitForExit();
        }

        _process.Dispose();
    }
}

/// <summary>Ядро с указанными модулями без загрузки из папки: проверкам нужен доступ к внутренностям модуля.</summary>
internal sealed class HardshipHarness : IDisposable
{
    private readonly ModuleHost _host;

    public HardshipHarness(TempDirectory temp, RecordingLog log, params IModule[] modules)
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
