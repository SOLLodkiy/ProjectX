using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ADHDWindows.App.Hud;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Storage;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

public sealed class HudTests : IDisposable
{
    private static readonly ScreenArea Main = Screen(0, 0, 1920, 1080, primary: true);
    private static readonly ScreenArea Right = Screen(1920, 0, 2560, 1440, primary: false);
    private static readonly ScreenArea[] Both = [Main, Right];

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private static ScreenArea Screen(int left, int top, int width, int height, bool primary) =>
        new(new ScreenRect(left, top, width, height), new ScreenRect(left, top, width, height - 40), 96, primary);

    [Fact]
    public void Position_inside_a_screen_is_kept()
    {
        var position = HudPlacement.Keep(new ScreenRect(2000, 100, 150, 44), Both);

        Assert.Equal(new ScreenPoint(2000, 100), position);
    }

    [Fact]
    public void Disconnected_monitor_returns_the_hud_to_a_visible_screen()
    {
        ScreenArea[] onlyMain = [Main];

        var position = HudPlacement.Keep(new ScreenRect(3000, 500, 150, 44), onlyMain);

        Assert.True(Main.WorkArea.Contains(new ScreenRect(position.X, position.Y, 150, 44)));
    }

    [Fact]
    public void Hud_stays_above_the_taskbar()
    {
        var position = HudPlacement.Keep(new ScreenRect(100, 1060, 150, 44), [Main]);

        Assert.Equal(1040 - 44, position.Y);
    }

    [Fact]
    public void Hud_on_a_monitor_seam_moves_to_the_monitor_holding_most_of_it()
    {
        var position = HudPlacement.Keep(new ScreenRect(1900, 100, 150, 44), Both);

        Assert.Equal(1920, position.X);
    }

    [Fact]
    public void Hud_in_a_corner_gap_goes_to_the_nearest_monitor()
    {
        // Правый монитор выше основного, поэтому под ним есть пустота.
        var position = HudPlacement.Keep(new ScreenRect(3000, 1420, 150, 44), Both);

        Assert.True(Right.WorkArea.Contains(new ScreenRect(position.X, position.Y, 150, 44)));
    }

    [Fact]
    public void Default_position_is_the_top_right_of_the_primary_screen()
    {
        var position = HudPlacement.Default(150, 44, 16, [Right, Main]);

        Assert.Equal(new ScreenPoint(1920 - 150 - 16, 16), position);
    }

    [Fact]
    public void Configuration_key_ignores_order_and_sees_changes()
    {
        Assert.Equal(HudPlacement.ConfigurationKey([Main, Right]), HudPlacement.ConfigurationKey([Right, Main]));
        Assert.NotEqual(HudPlacement.ConfigurationKey([Main]), HudPlacement.ConfigurationKey(Both));
        Assert.NotEqual(
            HudPlacement.ConfigurationKey([Main]),
            HudPlacement.ConfigurationKey([Main with { Dpi = 144 }]));
    }

    [Fact]
    public void Panel_opens_below_and_flips_at_the_bottom()
    {
        var top = new ScreenRect(100, 100, 150, 44);
        var bottom = new ScreenRect(100, 990, 150, 44);

        Assert.Equal(new ScreenPoint(100, 148), HudPlacement.PanelOrigin(top, 300, 200, 4, Both));
        Assert.Equal(new ScreenPoint(100, 786), HudPlacement.PanelOrigin(bottom, 300, 200, 4, Both));
    }

    [Fact]
    public void Panel_stays_on_the_screen_of_the_bar()
    {
        var bar = new ScreenRect(1760, 100, 150, 44);

        var origin = HudPlacement.PanelOrigin(bar, 300, 200, 4, Both);

        Assert.Equal(1920 - 300, origin.X);
    }

    [Fact]
    public void Coin_flies_down_only_without_room_above()
    {
        Assert.True(HudPlacement.FliesDown(new ScreenRect(100, 10, 150, 44), 56, Both));
        Assert.False(HudPlacement.FliesDown(new ScreenRect(100, 100, 150, 44), 56, Both));
    }

    [Fact]
    public void Hover_opens_after_a_delay_and_survives_the_move_to_the_panel()
    {
        var hover = new HudHover();
        var start = DateTimeOffset.UnixEpoch;

        Assert.False(hover.Update(true, false, start));
        Assert.False(hover.Update(true, false, start + TimeSpan.FromMilliseconds(100)));
        Assert.True(hover.Update(true, false, start + HudHover.OpenDelay));

        // Курсор ушёл с ручки и через мгновение оказался на панели.
        var left = start + TimeSpan.FromSeconds(1);
        Assert.True(hover.Update(false, false, left));
        Assert.True(hover.Update(false, true, left + TimeSpan.FromMilliseconds(100)));
        Assert.True(hover.Update(false, true, left + TimeSpan.FromSeconds(5)));

        var gone = left + TimeSpan.FromSeconds(6);
        Assert.True(hover.Update(false, false, gone));
        Assert.False(hover.Update(false, false, gone + HudHover.CloseDelay));
    }

    [Fact]
    public void Quick_pass_over_the_handle_does_not_open_the_panel()
    {
        var hover = new HudHover();
        var start = DateTimeOffset.UnixEpoch;

        hover.Update(true, false, start);
        hover.Update(false, false, start + TimeSpan.FromMilliseconds(80));

        Assert.False(hover.Update(true, false, start + TimeSpan.FromMilliseconds(160)));
    }

    [Fact]
    public void Settings_have_defaults_and_stay_in_range()
    {
        var settings = new HudSettings(new JsonFileStore(_temp.Combine("settings.json"), _log));
        var changes = 0;
        settings.Changed += () => changes++;

        Assert.True(settings.Visible);
        Assert.True(settings.ShowFlyouts);
        Assert.False(settings.HideInFullscreen);
        Assert.Equal(100, settings.Opacity);

        settings.Scale = 99;
        settings.Opacity = 1;

        Assert.Equal(HudSettings.MaxScale, settings.Scale);
        Assert.Equal(HudSettings.MinOpacity, settings.Opacity);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Position_is_stored_per_monitor_configuration_and_survives_a_restart()
    {
        var path = _temp.Combine("settings.json");
        var store = new JsonFileStore(path, _log);
        var settings = new HudSettings(store);
        settings.RememberPosition("one", new ScreenPoint(10, 20));
        settings.RememberPosition("two", new ScreenPoint(30, 40));
        store.Flush();

        var reloaded = new JsonFileStore(path, _log);
        reloaded.Load();
        var restored = new HudSettings(reloaded);

        Assert.Equal(new ScreenPoint(10, 20), restored.Position("one"));
        Assert.Equal(new ScreenPoint(30, 40), restored.Position("two"));
        Assert.Equal(new ScreenPoint(30, 40), restored.Position("новая конфигурация"));
    }

    [Fact]
    public void Position_is_empty_on_the_first_run()
    {
        var settings = new HudSettings(new JsonFileStore(_temp.Combine("settings.json"), _log));

        Assert.Null(settings.Position("any"));
    }

    [Fact]
    public void Off_screen_position_is_brought_back_by_the_real_window() => WithWindow(
        arrange: settings => settings.RememberPosition(
            HudPlacement.ConfigurationKey(Desktop.Screens()),
            new ScreenPoint(1_000_000, 1_000_000)),
        act: window =>
        {
            Assert.Contains(Desktop.Screens(), screen => screen.WorkArea.Contains(window.BarBounds));
            Assert.Contains(Desktop.Screens(), screen => screen.WorkArea.Overlap(WindowTraits.Bounds(Handle(window))) > 0);
        });

    [Fact]
    public void Clicks_pass_through_the_real_window() => WithWindow(
        arrange: _ => { },
        act: window =>
        {
            var bar = window.BarBounds;
            var point = new NativePoint { X = bar.Left + (bar.Width / 2), Y = bar.Top + (bar.Height / 2) };

            Assert.NotEqual(Handle(window), WindowFromPoint(point));
        });

    [Fact]
    public void Burst_of_income_flies_as_one_coin() => WithWindow(
        arrange: _ => { },
        act: (window, economy) =>
        {
            for (var index = 0; index < 50; index++)
            {
                economy.Earn("click", 1);
            }

            Pump();

            Assert.Equal(1, window.FlyoutsLaunched);
        });

    [Fact]
    public void Flyouts_can_be_turned_off() => WithWindow(
        arrange: settings => settings.ShowFlyouts = false,
        act: (window, economy) =>
        {
            economy.Earn("click", 1);

            Pump();

            Assert.Equal(0, window.FlyoutsLaunched);
        });

    [Fact]
    public void Unplugged_monitor_message_moves_the_real_window_to_a_visible_screen() => WpfThread.Run(() =>
    {
        var real = Desktop.Screens();
        var extra = Screen(100_000, 0, 1920, 1080, primary: false);
        IReadOnlyList<ScreenArea> attached = [.. real, extra];
        var bus = new EventBus(_log);
        var store = new JsonFileStore(_temp.Combine("hud.json"), _log);
        var settings = new HudSettings(store);
        settings.RememberPosition(HudPlacement.ConfigurationKey(attached), new ScreenPoint(100_500, 500));
        var economy = new CoinEconomy(store, bus, TimeProvider.System);
        var texts = new TextCatalog(new GameData(_temp.Path, _log), "ru", _log);
        using var window = new HudWindow(economy, bus, texts, settings, _log, screens: () => attached);

        window.Start();
        Assert.Equal(100_500, window.BarBounds.Left);

        attached = real;
        SendMessageW(Handle(window), Desktop.DisplayChangedMessage, IntPtr.Zero, IntPtr.Zero);
        Pump();

        Assert.Contains(real, screen => screen.WorkArea.Contains(window.BarBounds));
    });

    [Fact]
    public void Hud_can_be_hidden_and_shown_again() => WpfThread.Run(() =>
    {
        var bus = new EventBus(_log);
        var store = new JsonFileStore(_temp.Combine("hud.json"), _log);
        var settings = new HudSettings(store);
        var economy = new CoinEconomy(store, bus, TimeProvider.System);
        var texts = new TextCatalog(new GameData(_temp.Path, _log), "ru", _log);
        using var window = new HudWindow(economy, bus, texts, settings, _log);
        window.Start();
        Assert.True(window.IsVisible);

        settings.Visible = false;
        Assert.False(window.IsVisible);

        settings.Visible = true;
        Assert.True(window.IsVisible);
    });

    [Fact]
    public void Hidden_hud_shows_no_window() => WithWindow(
        arrange: settings => settings.Visible = false,
        act: window => Assert.False(window.IsVisible));

    private void WithWindow(Action<HudSettings> arrange, Action<HudWindow> act) =>
        WithWindow(arrange, (window, _) => act(window));

    private void WithWindow(Action<HudSettings> arrange, Action<HudWindow, CoinEconomy> act) => WpfThread.Run(() =>
    {
        var bus = new EventBus(_log);
        var store = new JsonFileStore(_temp.Combine("hud.json"), _log);
        var data = new GameData(_temp.Path, _log);
        var settings = new HudSettings(store);
        arrange(settings);

        var economy = new CoinEconomy(store, bus, TimeProvider.System);
        var window = new HudWindow(economy, bus, new TextCatalog(data, "ru", _log), settings, _log);
        try
        {
            window.Start();
            act(window, economy);
        }
        finally
        {
            window.Dispose();
        }
    });

    /// <summary>Даёт выполниться всему, что уже передано в поток интерфейса.</summary>
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
