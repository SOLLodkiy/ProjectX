using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ADHDWindows.App.Design;

/// <summary>Содержимое витрины компонентов: всё, что есть в дизайн-системе.</summary>
public partial class ShowcaseContent : UserControl
{
    private const int SwatchSize = 14;

    private static readonly string[] ListSprites = ["coin", "lock", "unlock", "star", "trophy", "chart", "quest", "clock", "gear", "heart", "shop", "chest"];

    public ShowcaseContent()
    {
        InitializeComponent();

        var assets = PixelAssets.Current;
        foreach (var color in assets.Palette.Colors)
        {
            PaletteRow.Children.Add(new Border
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Margin = new Thickness(0, 0, 1, 1),
                Background = assets.Brush(color.Name),
            });
        }

        var sprites = assets.Atlas.Sprites
            .Where(sprite => sprite.Slice == 0)
            .OrderBy(sprite => sprite.Height)
            .ThenBy(sprite => sprite.Name, StringComparer.Ordinal);
        foreach (var sprite in sprites)
        {
            SpriteRow.Children.Add(new PixelImage { Sprite = sprite.Name, Margin = new Thickness(0, 0, 2, 2) });
        }

        var names = (PixelAssets.Texts?.Get("showcase.list.items") ?? "").Split('|');
        for (var index = 0; index < ListSprites.Length; index++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new PixelImage { Sprite = ListSprites[index], Margin = new Thickness(0, 0, 4, 0) });
            row.Children.Add(new PixelText
            {
                Text = index < names.Length ? names[index] : ListSprites[index],
                VerticalAlignment = VerticalAlignment.Center,
            });
            SampleList.Items.Add(row);
        }

        SampleList.SelectedIndex = 1;
        Loaded += (_, _) => UpdateScaleInfo();
    }

    /// <summary>Переключает вкладку, чтобы проверки могли отрисовать каждую.</summary>
    public void SelectTab(int index) => Tabs.SelectedIndex = index;

    public int TabCount => Tabs.Items.Count;

    public void PlayCoin() =>
        PixelAnimations.FlyUp(EffectLayer, "coin", EffectTarget.TranslatePoint(new Point(4, 2), EffectLayer));

    private static string Text(string key) => PixelAssets.Texts?.Get(key) ?? key;

    private void OnToastInfo(object sender, RoutedEventArgs e) =>
        ToastManager.Show(ToastKind.Info, Text("showcase.toast.info.title"), Text("showcase.toast.info.text"), "clock");

    private void OnToastReward(object sender, RoutedEventArgs e) =>
        ToastManager.Show(ToastKind.Reward, Text("showcase.toast.reward.title"), Text("showcase.toast.reward.text"), "coin");

    private void OnToastDanger(object sender, RoutedEventArgs e) =>
        ToastManager.Show(ToastKind.Danger, Text("showcase.toast.danger.title"), Text("showcase.toast.danger.text"), "warning");

    private void OnShake(object sender, RoutedEventArgs e) => PixelAnimations.Shake(EffectTarget);

    private void OnFlash(object sender, RoutedEventArgs e) => PixelAnimations.Flash(EffectTarget);

    private void OnCoin(object sender, RoutedEventArgs e) => PlayCoin();

    private void OnScale(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is PixelWindow window && sender is FrameworkElement { Tag: string tag })
        {
            window.UiScale = int.Parse(tag);
            window.Dispatcher.BeginInvoke(UpdateScaleInfo, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void UpdateScaleInfo()
    {
        var root = FindRoot(this);
        var percent = (int)Math.Round(VisualTreeHelper.GetDpi(this).DpiScaleX * 100);
        ScaleInfo.Text = PixelAssets.Texts?.Get("showcase.scale.info", percent, root?.DevicePixels ?? 0) ?? "";
    }

    private static PixelRoot? FindRoot(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is PixelRoot root)
            {
                return root;
            }
        }

        return null;
    }
}
