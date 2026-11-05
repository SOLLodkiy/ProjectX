using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ADHDWindows.App.Design;

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
