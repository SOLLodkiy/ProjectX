using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ADHDWindows.App.Design;
using ADHDWindows.App.Hud;
using ADHDWindows.App.Quests;
using ADHDWindows.Core.Quests;
using ADHDWindows.App.Shop;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Upgrades;
using ADHDWindows.Modules.Upgrades;

namespace ADHDWindows.Tests;

/// <summary>
/// Отрисовывает компоненты настоящим WPF при заданном масштабе монитора и проверяет чёткость:
/// только цвета палитры и равные квадраты вместо пикселей арта.
/// </summary>
public class PixelRenderTests
{
    /// <summary>Если переменная задана, снимки сохраняются в эту папку для просмотра глазами.</summary>
    private const string SnapshotVariable = "ADHDWINDOWS_SNAPSHOTS";

    [Theory]
    [InlineData(1.0, 2)]
    [InlineData(1.25, 3)]
    [InlineData(1.5, 3)]
    [InlineData(2.0, 4)]
    public void Showcase_stays_on_the_pixel_grid(double dpiScale, int devicePixels) => WpfThread.Run(() =>
    {
        var content = new ShowcaseContent();
        for (var tab = 0; tab < content.TabCount; tab++)
        {
            content.SelectTab(tab);
            var frame = new PixelFrame { Sprite = "window", Padding = new Thickness(5), Child = content };
            AssertCrisp(frame, dpiScale, devicePixels, $"showcase-{tab}");
            frame.Child = null;
        }
    });

    [Theory]
    [InlineData(1.0, 2)]
    [InlineData(1.5, 3)]
    public void Centered_content_stays_on_the_pixel_grid(double dpiScale, int devicePixels) => WpfThread.Run(() =>
    {
        // Нечётная разница размеров ставит содержимое на половину пикселя арта.
        var button = new PixelButton
        {
            Content = "Купить",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var host = new Grid { Width = 101, Height = 51, Children = { button } };
        AssertCrisp(new PixelFrame { Sprite = "panel", Child = host }, dpiScale, devicePixels, "centered");
    });

    [Theory]
    [InlineData(ToastKind.Info)]
    [InlineData(ToastKind.Reward)]
    [InlineData(ToastKind.Success)]
    [InlineData(ToastKind.Danger)]
    public void Toasts_stay_on_the_pixel_grid(ToastKind kind) => WpfThread.Run(() =>
    {
        var card = new ToastCard(kind, "Задание выполнено", "Вы получили 150 монеток, поздравляем!", "coin");
        AssertCrisp(card, 1.5, 3, $"toast-{kind}");
    });

    [Theory]
    [InlineData(1.0, 2)]
    [InlineData(1.25, 3)]
    [InlineData(1.5, 3)]
    public void Hud_stays_on_the_pixel_grid(double dpiScale, int devicePixels) => WpfThread.Run(() =>
    {
        var bar = new HudBar();
        bar.ShowBalance(123_456_789);
        AssertCrisp(bar, dpiScale, devicePixels, "hud-bar");

        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), new RecordingLog());
        data.Load();
        var texts = new TextCatalog(data, "ru", new RecordingLog());
        texts.Use("ru");
        var panel = new HudPanel(texts);
        panel.Show(123_456_789, 1250, "Сильная гравитация", null);
        AssertCrisp(panel, dpiScale, devicePixels, "hud-panel");
    });

    [Theory]
    [InlineData(1.0, 2)]
    [InlineData(1.5, 3)]
    public void Shop_stays_on_the_pixel_grid(double dpiScale, int devicePixels) => WpfThread.Run(() =>
    {
        var log = new RecordingLog();
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), log);
        data.Load();
        var texts = new TextCatalog(data, "ru", log);
        texts.Use("ru");
        var bus = new EventBus(log);
        var store = new MemoryStore();
        var economy = new CoinEconomy(store, bus, TimeProvider.System);
        var shop = new UpgradeShop(UpgradeCatalog.Load(data, log), economy, bus, store, TimeProvider.System, log);
        economy.Earn("developer", 190);
        shop.Buy("clicks.left.plus");

        var window = new ShopWindow(texts, _ => { });
        window.Update(shop.Snapshot(), economy.Balance);

        // Второй уровень по карману, ПКМ открыт, но дорог, «Тяжёлая рука» закрыта требованием.
        Assert.True(window.Rows["clicks.left.plus"].Buy.IsEnabled);
        Assert.False(window.Rows["clicks.right"].Buy.IsEnabled);
        Assert.False(window.Rows["clicks.power"].Buy.IsEnabled);
        economy.Earn("developer", 1000);
        window.ShowBalance(economy.Balance);
        Assert.True(window.Rows["clicks.right"].Buy.IsEnabled);
        Assert.False(window.Rows["clicks.power"].Buy.IsEnabled);

        var content = (UIElement)window.Content;
        window.Content = null;
        for (var branch = 0; branch < window.BranchCount; branch++)
        {
            window.SelectBranch(branch);
            var frame = new PixelFrame { Sprite = "window", Padding = new Thickness(5), Child = content };
            AssertCrisp(frame, dpiScale, devicePixels, $"shop-{branch}");
            frame.Child = null;
        }

        window.Close();
    });

    [Theory]
    [InlineData(1.0, 2)]
    [InlineData(1.5, 3)]
    public void Stats_window_stays_on_the_pixel_grid(double dpiScale, int devicePixels) => WpfThread.Run(() =>
    {
        var log = new RecordingLog();
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), log);
        data.Load();
        var texts = new TextCatalog(data, "ru", log);
        texts.Use("ru");
        var clock = new ManualClock();
        var recorder = new ADHDWindows.Modules.Stats.StatsRecorder(new MemoryStore(), clock);
        var when = clock.GetUtcNow();
        recorder.OnEarned(new CoinsEarned("click", 1234, 1234));
        recorder.OnEarned(new CoinsEarned("video", 400, 1634));
        recorder.OnClick(new ADHDWindows.Core.Input.ClickCounted(ADHDWindows.Core.Input.MouseButton.Left, TimeSpan.Zero));
        recorder.OnMedia(new ADHDWindows.Core.Media.MediaTimeCounted("video", "youtube.com", 4000));
        recorder.OnCollection(new ADHDWindows.Core.Locks.CollectionChanged(
            [new ADHDWindows.Core.Locks.CollectionItem("site:youtube.com", "YouTube", ADHDWindows.Core.Locks.LockKind.Site, 800, when)],
            [new ADHDWindows.Core.Locks.CollectionCatalog(ADHDWindows.Core.Locks.LockKind.Site, 10, 1)]));
        recorder.OnUpgrade(new UpgradePurchased("clicks.left.plus", "Крепкий палец", 1, 60, when), "Улучшение «Крепкий палец»");
        recorder.OnCounter(new ADHDWindows.Core.Stats.StatCounted("quests.completed.easy", 2));
        clock.Advance(TimeSpan.FromDays(1));
        recorder.OnEarned(new CoinsEarned("click", 300, 1934));

        var window = new ADHDWindows.App.Stats.StatsWindow(texts, () => { }, () => { });
        window.Update(recorder.Snapshot(1934));

        // Счётчики окна совпадают со статистикой, разблокированное — с датой и ценой.
        Assert.Contains("1934", window.TextsOf("summary"));
        Assert.Contains($"1634 ({when.ToLocalTime():dd.MM.yyyy})", window.TextsOf("summary"));
        Assert.Contains("1 ч 6 мин", window.TextsOf("summary"));
        Assert.Contains($"{when.ToLocalTime():dd.MM.yyyy} — 800", window.TextsOf("collection"));
        Assert.Contains("Открыто 10% каталога", window.TextsOf("collection"));
        Assert.Contains("2", window.TextsOf("quests"));
        Assert.Equal(ADHDWindows.App.Stats.StatsWindow.DayCount, window.ChartValues.Count);
        Assert.Equal([1634L, 300L], window.ChartValues.TakeLast(2));
        window.ShowWeeks(true);
        Assert.Equal(ADHDWindows.App.Stats.StatsWindow.WeekCount, window.ChartValues.Count);
        Assert.Equal(1934, window.ChartValues.Sum());
        window.ShowWeeks(false);

        var content = (UIElement)window.Content;
        window.Content = null;
        for (var tab = 0; tab < window.TabCount; tab++)
        {
            window.SelectTab(tab);
            var frame = new PixelFrame { Sprite = "window", Padding = new Thickness(5), Child = content };
            AssertCrisp(frame, dpiScale, devicePixels, $"stats-{tab}");
            frame.Child = null;
        }

        window.Close();
    });

    [Theory]
    [InlineData(1.0, 2)]
    [InlineData(1.5, 3)]
    public void Quests_window_stays_on_the_pixel_grid(double dpiScale, int devicePixels) => WpfThread.Run(() =>
    {
        var log = new RecordingLog();
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), log);
        data.Load();
        var texts = new TextCatalog(data, "ru", log);
        texts.Use("ru");
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var window = new QuestsWindow(texts);
        window.Update(
            [
                new QuestView("e", "easy.listen", QuestTier.Easy, "Прослушай трек полностью", "clock", 45, 200, QuestUnit.Count, 35, now, now.AddMinutes(12.5), false, "https://soundcloud.com/forss/flickermood"),
                new QuestView("h", "hard.game.one", QuestTier.Hard, "Поиграй в Minecraft 30 минут", "trophy", 1800, 1800, QuestUnit.Seconds, 450, now, now.AddMinutes(61), true),
            ],
            now,
            chain: 1,
            chainGoal: 3);

        var easy = window.Rows[QuestTier.Easy];
        Assert.Equal(["Прослушай трек полностью", "45/200", "Награда: 35", "Новое через 12:30"], easy.Texts);
        Assert.Equal(Visibility.Visible, easy.OpenButton.Visibility);
        Assert.Equal("Открыть", easy.OpenButton.Content);
        Assert.Equal(Visibility.Collapsed, window.Rows[QuestTier.Hard].OpenButton.Visibility);
        Assert.Equal(Visibility.Collapsed, window.Rows[QuestTier.Medium].OpenButton.Visibility);
        Assert.Equal("Цепочка: 1/3", window.ChainText);
        Assert.True(QuestRow.IsWebLink("https://ru.wikipedia.org/wiki/Огурец"));
        Assert.False(QuestRow.IsWebLink("file:///C:/Windows/notepad.exe"));
        Assert.False(QuestRow.OpenLink("calc.exe"));
        Assert.Equal(0.225, easy.Share, 3);
        Assert.Equal(["Поиграй в Minecraft 30 минут", "30:00/30:00", "Выполнено!", "Новое через 1:01:00"], window.Rows[QuestTier.Hard].Texts);
        Assert.Equal("Нет выполнимых заданий. Новое появится позже.", window.Rows[QuestTier.Medium].Texts[0]);
        window.Tick(now.AddMinutes(2));
        Assert.Equal("Новое через 10:30", easy.Texts[3]);

        var content = (UIElement)window.Content;
        window.Content = null;
        var frame = new PixelFrame { Sprite = "window", Padding = new Thickness(5), Child = content };
        AssertCrisp(frame, dpiScale, devicePixels, "quests");
        frame.Child = null;
        window.Close();
    });

    [Fact]
    public void Reset_button_waits_before_it_works() => WpfThread.Run(() =>
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), new RecordingLog());
        data.Load();
        var texts = new TextCatalog(data, "ru", new RecordingLog());
        texts.Use("ru");
        var window = new ADHDWindows.App.Stats.ResetWindow(texts);
        for (var second = 0; second < ADHDWindows.App.Stats.ResetWindow.DelaySeconds; second++)
        {
            Assert.False(window.Confirm.IsEnabled);
            window.Tick();
        }

        Assert.True(window.Confirm.IsEnabled);
        Assert.Equal("Сбросить всё", window.Confirm.Content);

        var content = (UIElement)window.Content;
        window.Content = null;
        AssertCrisp(new PixelFrame { Sprite = "window", Padding = new Thickness(5), Child = content }, 1.5, 3, "reset");
        window.Close();
    });

    [Fact]
    public void Chart_bars_have_whole_heights()
    {
        Assert.Equal(0, PixelBarChart.BarHeight(0, 100, 40));
        Assert.Equal(1, PixelBarChart.BarHeight(1, 1_000_000, 40));
        Assert.Equal(40, PixelBarChart.BarHeight(100, 100, 40));
        Assert.Equal(20, PixelBarChart.BarHeight(50, 100, 40));
        Assert.Equal(1, PixelMeter.Filled(0.0001, 300));
        Assert.Equal(300, PixelMeter.Filled(1, 300));
        Assert.Equal(0, PixelMeter.Filled(0, 300));
    }

    [Fact]
    public void Root_follows_monitor_scale() => WpfThread.Run(() =>
    {
        var root = new PixelRoot { Child = new PixelFrame { Width = 10, Height = 10 } };
        VisualTreeHelper.SetRootDpi(root, new DpiScale(1.5, 1.5));
        root.UpdateScale();
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(3, root.DevicePixels);
        Assert.Equal(20, root.DesiredSize.Width, 6);

        root.UiScale = 3;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(5, root.DevicePixels);
    });

    [Fact]
    public void Showcase_window_opens_and_closes() => WpfThread.Run(() =>
    {
        var window = new ShowcaseWindow();
        window.Show();
        window.UpdateLayout();
        Assert.Equal(WindowStyle.None, window.WindowStyle);
        Assert.True(window.ActualWidth > 0);

        var content = (ShowcaseContent)window.Content;
        content.SelectTab(content.TabCount - 1);
        window.UpdateLayout();
        content.PlayCoin();

        window.Close();
    });

    [Fact]
    public void Button_frame_follows_kind_and_state() => WpfThread.Run(() =>
    {
        var button = new PixelButton();
        Assert.Equal("button", button.FrameSprite);

        button.Kind = PixelButtonKind.Primary;
        Assert.Equal("button_primary", button.FrameSprite);

        button.IsEnabled = false;
        Assert.Equal("button_disabled", button.FrameSprite);
    });

    private static void AssertCrisp(UIElement child, double dpiScale, int devicePixels, string name)
    {
        var pixels = Render(child, dpiScale, out var width, out var height);
        Snapshot(pixels, width, height, $"{name}-{dpiScale * 100}");

        var allowed = PixelAssets.Current.Palette.Colors.Select(color => color.Argb).ToHashSet();
        for (var index = 0; index < pixels.Length; index++)
        {
            var pixel = pixels[index];
            Assert.True(
                pixel >> 24 == 0 || allowed.Contains(pixel),
                $"{name}: цвет {pixel:X8} вне палитры в точке ({index % width}, {index / width})");
        }

        Assert.Equal(0, width % devicePixels);
        Assert.Equal(0, height % devicePixels);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cell = ((y - (y % devicePixels)) * width) + x - (x % devicePixels);
                Assert.True(
                    pixels[(y * width) + x] == pixels[cell],
                    $"{name}: пиксель арта неоднороден в точке ({x}, {y}) при {dpiScale * 100}%");
            }
        }
    }

    private static uint[] Render(UIElement child, double dpiScale, out int width, out int height)
    {
        var root = new PixelRoot { Child = child };
        VisualTreeHelper.SetRootDpi(root, new DpiScale(dpiScale, dpiScale));
        root.UpdateScale();
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();

        // Второй проход применяет привязку к сетке, вычисленную после первого.
        root.UpdateLayout();

        width = (int)Math.Round(root.DesiredSize.Width * dpiScale);
        height = (int)Math.Round(root.DesiredSize.Height * dpiScale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var pixels = new uint[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        root.Child = null;
        return pixels;
    }

    private static void Snapshot(uint[] pixels, int width, int height, string name)
    {
        var directory = Environment.GetEnvironmentVariable(SnapshotVariable);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, $"{name}.png"));
        encoder.Save(file);
    }
}
