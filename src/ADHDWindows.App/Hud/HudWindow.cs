using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ADHDWindows.App.Design;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Display;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hud;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Pixels;
using ADHDWindows.Core.Stats;
using ADHDWindows.Platform;

namespace ADHDWindows.App.Hud;

/// <summary>
/// HUD поверх всех окон. Клики проходят сквозь него, кроме ручки: окно не получает сообщений мыши,
/// пока на него пропускаются клики, поэтому положение курсора опрашивается по таймеру.
/// События экономики приходят из любого потока и передаются в поток интерфейса.
/// </summary>
public sealed class HudWindow : Window, IDisposable
{
    /// <summary>Зона над полоской и под ней, где летают монетки.</summary>
    private const int FlightZone = 28;

    private const int ScreenMargin = 8;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan FlyoutInterval = TimeSpan.FromMilliseconds(120);
    private const int SlowPollEvery = 12;

    private readonly IEconomy _economy;
    private readonly HudSettings _settings;
    private readonly ILog _log;
    private readonly TimeProvider _clock;
    private readonly Func<IReadOnlyList<ScreenArea>> _screens;
    private readonly bool _realScreens;
    private readonly HudBar _bar = new();
    private readonly HudPanel _panel;
    private readonly Canvas _flights = new();
    private readonly PixelRoot _root;
    private readonly HudHover _hover = new();
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _flyoutTimer;
    private readonly List<IDisposable> _subscriptions = [];
    private HudPanelWindow? _panelWindow;
    private Func<bool>? _shopAvailable;
    private Func<bool>? _logsAvailable;
    private Func<bool>? _questsAvailable;
    private Func<bool>? _progressAvailable;
    private string? _configuration;
    private ScreenPoint _position;
    private ScreenPoint? _grab;
    private string? _hardship;
    private string? _quest;
    private long _pendingCoins;
    private int _refreshQueued;
    private int _pollCount;
    private bool _clickThrough = true;
    private bool _fullscreen;
    private bool _disposed;

    public HudWindow(
        IEconomy economy,
        IEventBus events,
        TextCatalog texts,
        HudSettings settings,
        ILog log,
        TimeProvider? clock = null,
        Func<IReadOnlyList<ScreenArea>>? screens = null)
    {
        _economy = economy;
        _settings = settings;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _screens = screens ?? Desktop.Screens;
        _realScreens = screens is null;
        _panel = new HudPanel(texts);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = null;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Cursor = Cursors.SizeAll;

        var layer = new Canvas { Width = HudBar.BarWidth, Height = HudBar.BarHeight + (2 * FlightZone) };
        Canvas.SetTop(_bar, FlightZone);
        layer.Children.Add(_bar);
        _flights.IsHitTestVisible = false;
        layer.Children.Add(_flights);
        _root = new PixelRoot { Child = layer };
        Content = _root;

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _poll.Tick += (_, _) => Poll();
        _flyoutTimer = new DispatcherTimer { Interval = FlyoutInterval };
        _flyoutTimer.Tick += (_, _) => LaunchFlyout();

        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => PlaceAt(_position));
        MouseLeftButtonDown += OnGrab;
        MouseMove += OnDrag;
        MouseLeftButtonUp += OnDrop;
        LostMouseCapture += OnRelease;

        _settings.Changed += ApplySettings;
        _subscriptions.Add(events.Subscribe<CoinsEarned>(OnEarned));
        _subscriptions.Add(events.Subscribe<CoinsSpent>(_ => QueueRefresh()));
        _subscriptions.Add(events.Subscribe<HudStatusChanged>(OnStatusChanged));
        _subscriptions.Add(events.Subscribe<ProgressReset>(_ => QueueRefresh()));
    }

    /// <summary>Положение полоски на экране.</summary>
    public ScreenRect BarBounds => BarRect(_position);

    /// <summary>Сколько монеток вылетело с момента запуска.</summary>
    public int FlyoutsLaunched { get; private set; }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    private int ArtPixel => _root.DevicePixels;

    private ScreenRect HandleBounds => new(_position.X, _position.Y, HudBar.HandleWidth * ArtPixel, HudBar.BarHeight * ArtPixel);

    /// <summary>
    /// Подключает кнопку «Магазин» мини-панели. Кнопка доступна, пока <paramref name="available"/> возвращает true:
    /// без модуля магазина открывать нечего.
    /// </summary>
    public void ConnectShop(Action open, Func<bool> available)
    {
        _shopAvailable = available;
        _panel.Shop.Click += (_, _) =>
        {
            CloseMiniPanel();
            open();
        };
    }

    /// <summary>Подключает кнопку «Логи» мини-панели. Без модуля статистики кнопка выключена.</summary>
    public void ConnectLogs(Action open, Func<bool> available)
    {
        _logsAvailable = available;
        _panel.Logs.Click += (_, _) =>
        {
            CloseMiniPanel();
            open();
        };
    }

    /// <summary>Подключает кнопку «Задания» мини-панели. Без модуля заданий кнопка выключена.</summary>
    public void ConnectQuests(Action open, Func<bool> available)
    {
        _questsAvailable = available;
        _panel.Quests.Click += (_, _) =>
        {
            CloseMiniPanel();
            open();
        };
    }

    /// <summary>Подключает кнопку «Прогресс» мини-панели. Без модуля прогресса кнопка выключена.</summary>
    public void ConnectProgress(Action open, Func<bool> available)
    {
        _progressAvailable = available;
        _panel.Progress.Click += (_, _) =>
        {
            CloseMiniPanel();
            open();
        };
    }

    /// <summary>Показывает HUD, если это разрешено настройками.</summary>
    public void Start()
    {
        EnsureInitialized();
        Refresh();
        ApplySettings();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= ApplySettings;
        _subscriptions.ForEach(subscription => subscription.Dispose());
        _poll.Stop();
        _flyoutTimer.Stop();
        _panelWindow?.Close();
        Close();
    }

    private void EnsureInitialized() => new WindowInteropHelper(this).EnsureHandle();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowTraits.MakeNonActivating(Handle);
        WindowTraits.SetClickThrough(Handle, true);
        HwndSource.FromHwnd(Handle).AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var changed = message == Desktop.DisplayChangedMessage
            || (message == Desktop.SettingChangedMessage && wParam.ToInt64() == Desktop.WorkAreaSetting);
        if (changed)
        {
            // Система рассылает сообщение до того, как обновит данные всех мониторов.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, FollowScreens);
        }

        return IntPtr.Zero;
    }

    /// <summary>Мониторы изменились: положение берётся для новой конфигурации или сдвигается на видимый экран.</summary>
    private void FollowScreens()
    {
        var screens = _screens();
        var configuration = HudPlacement.ConfigurationKey(screens);
        if (configuration != _configuration)
        {
            _log.Info($"Конфигурация мониторов изменилась: {configuration}");
            PlaceAt(_settings.Position(configuration) ?? _position, screens);
        }
        else
        {
            PlaceAt(_position, screens);
        }

        _configuration = configuration;
        CloseMiniPanel();
    }

    private void PlaceInitially()
    {
        var screens = _screens();
        _configuration = HudPlacement.ConfigurationKey(screens);
        // На тестовом мониторе HUD встаёт туда же, куда на основном: в правый верхний угол.
        var home = _realScreens && TestScreen.Find() is { } test ? [test with { IsPrimary = true }] : screens;
        var fallback = HudPlacement.Default(
            HudBar.BarWidth * ArtPixel,
            HudBar.BarHeight * ArtPixel,
            ScreenMargin * ArtPixel,
            home);
        PlaceAt(_settings.Position(_configuration) ?? fallback, screens);
    }

    private void PlaceAt(ScreenPoint position) => PlaceAt(position, _screens());

    private void PlaceAt(ScreenPoint position, IReadOnlyList<ScreenArea> screens)
    {
        if (_disposed)
        {
            return;
        }

        _position = HudPlacement.Keep(BarRect(position), screens);
        WindowTraits.Move(Handle, new ScreenPoint(_position.X, _position.Y - (FlightZone * ArtPixel)));
    }

    private ScreenRect BarRect(ScreenPoint position) =>
        new(position.X, position.Y, HudBar.BarWidth * ArtPixel, HudBar.BarHeight * ArtPixel);

    private void ApplySettings()
    {
        Opacity = _settings.Opacity / 100.0;
        _root.UiScale = _settings.Scale;
        if (_settings.Visible)
        {
            _poll.Start();
        }
        else
        {
            _poll.Stop();
        }

        UpdateVisibility();
        if (IsVisible)
        {
            UpdateLayout();
            PlaceAt(_position);
        }
    }

    private void UpdateVisibility()
    {
        var wanted = _settings.Visible && !(_settings.HideInFullscreen && _fullscreen);
        if (wanted == IsVisible)
        {
            return;
        }

        if (wanted)
        {
            var first = _configuration is null;
            Show();
            UpdateLayout();
            if (first)
            {
                PlaceInitially();
            }
        }
        else
        {
            CloseMiniPanel();
            Hide();
        }
    }

    private void Poll()
    {
        if (++_pollCount % SlowPollEvery == 0)
        {
            SlowPoll();
        }

        if (_grab is not null || !IsVisible)
        {
            return;
        }

        var cursor = Desktop.CursorPosition();
        var overHandle = HandleBounds.Contains(cursor);
        SetClickThrough(!overHandle);

        var overPanel = _panelWindow is { IsVisible: true } window && window.Bounds.Contains(cursor);
        var open = _hover.Update(overHandle, overPanel, _clock.GetUtcNow());
        if (open && _panelWindow is not { IsVisible: true })
        {
            OpenMiniPanel();
        }
        else if (!open && _panelWindow is { IsVisible: true })
        {
            _panelWindow.Hide();
        }
    }

    /// <summary>То, что не требует реакции на курсор, обновляется реже.</summary>
    private void SlowPoll()
    {
        _fullscreen = _settings.HideInFullscreen && Desktop.IsForegroundFullscreen();
        UpdateVisibility();
        if (_panelWindow is { IsVisible: true })
        {
            UpdatePanel();
        }
    }

    private void SetClickThrough(bool enabled)
    {
        if (_clickThrough != enabled)
        {
            _clickThrough = enabled;
            WindowTraits.SetClickThrough(Handle, enabled);
        }
    }

    private void OpenMiniPanel()
    {
        _panelWindow ??= new HudPanelWindow(_panel);
        UpdatePanel();
        _panelWindow.Opacity = Opacity;
        _panelWindow.Open(BarBounds, _settings.Scale, _screens());
    }

    private void CloseMiniPanel()
    {
        _hover.Reset();
        _panelWindow?.Hide();
    }

    private void UpdatePanel()
    {
        _panel.Shop.IsEnabled = _shopAvailable?.Invoke() == true;
        _panel.Logs.IsEnabled = _logsAvailable?.Invoke() == true;
        _panel.Quests.IsEnabled = _questsAvailable?.Invoke() == true;
        _panel.Progress.IsEnabled = _progressAvailable?.Invoke() == true;
        _panel.Show(_economy.Balance, _economy.IncomePerMinute, _hardship, _quest);
    }

    private void OnGrab(object sender, MouseButtonEventArgs e)
    {
        var cursor = Desktop.CursorPosition();
        if (!HandleBounds.Contains(cursor))
        {
            return;
        }

        CloseMiniPanel();
        _grab = new ScreenPoint(cursor.X - _position.X, cursor.Y - _position.Y);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (_grab is { } grab)
        {
            var cursor = Desktop.CursorPosition();
            PlaceAt(new ScreenPoint(cursor.X - grab.X, cursor.Y - grab.Y));
        }
    }

    private void OnDrop(object sender, MouseButtonEventArgs e)
    {
        // Последнее движение могло не дойти до окна, поэтому итоговое положение берётся от курсора.
        OnDrag(sender, e);
        ReleaseMouseCapture();
    }

    private void OnRelease(object sender, MouseEventArgs e)
    {
        if (_grab is not null)
        {
            _grab = null;
            _settings.RememberPosition(_configuration ?? "", _position);
        }
    }

    private void OnEarned(CoinsEarned earned)
    {
        Interlocked.Add(ref _pendingCoins, earned.Amount);
        QueueRefresh();
    }

    private void OnStatusChanged(HudStatusChanged status) => Dispatcher.BeginInvoke(() =>
    {
        if (status.Slot == HudSlot.Hardship)
        {
            _hardship = status.Text;
        }
        else
        {
            _quest = status.Text;
        }

        if (_panelWindow is { IsVisible: true })
        {
            UpdatePanel();
        }
    });

    /// <summary>Частые начисления объединяются в одно обновление экрана.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            Dispatcher.BeginInvoke(Refresh);
        }
    }

    private void Refresh()
    {
        Volatile.Write(ref _refreshQueued, 0);
        _bar.ShowBalance(_economy.Balance);
        if (_panelWindow is { IsVisible: true })
        {
            UpdatePanel();
        }

        if (!_flyoutTimer.IsEnabled)
        {
            LaunchFlyout();
        }
    }

    private void LaunchFlyout()
    {
        var coins = Interlocked.Exchange(ref _pendingCoins, 0);
        if (coins == 0 || _disposed || !_settings.ShowFlyouts || !IsVisible)
        {
            _flyoutTimer.Stop();
            return;
        }

        _flyoutTimer.Start();
        FlyoutsLaunched++;
        var down = HudPlacement.FliesDown(BarBounds, FlightZone * ArtPixel, _screens());
        var flyout = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        flyout.Children.Add(new PixelImage { Sprite = "coin" });
        flyout.Children.Add(new PixelText
        {
            Text = CoinFormat.Gain(coins),
            Shadow = true,
            Margin = new Thickness(1, 3, 0, 0),
            Foreground = PixelAssets.Current.Brush("gold"),
        });
        var start = new Point(HudBar.HandleWidth, FlightZone + (down ? HudBar.BarHeight - 16 : 0));
        PixelAnimations.FlyUp(_flights, flyout, start, down ? -FlightZone : FlightZone);
    }
}
