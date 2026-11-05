using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Pixels;

namespace ADHDWindows.App.Design;

public enum PixelFontRole
{
    Body,
    Title,
}

/// <summary>Палитра, шрифты и атлас спрайтов. Загружаются один раз при запуске из папки assets.</summary>
public sealed class PixelAssets
{
    private const int SlicePieces = 9;

    private readonly Dictionary<string, AtlasSprite> _sprites;
    private readonly Dictionary<(string Name, int Frame), ImageSource> _frames = [];
    private readonly Dictionary<string, ImageSource[]> _slices = [];
    private readonly BitmapSource _atlas;

    private PixelAssets(string assetsDir)
    {
        Palette = Palette.Parse(File.ReadAllText(Path.Combine(assetsDir, "palette.json")));
        BodyFont = PixelFont.Parse(File.ReadAllText(Path.Combine(assetsDir, "fonts", "body.json")));
        TitleFont = PixelFont.Parse(File.ReadAllText(Path.Combine(assetsDir, "fonts", "title.json")));

        var spritesDir = Path.Combine(assetsDir, "sprites");
        Atlas = SpriteAtlas.Parse(File.ReadAllText(Path.Combine(spritesDir, "atlas.json")));
        _sprites = Atlas.Sprites.ToDictionary(sprite => sprite.Name);
        _atlas = LoadBitmap(Path.Combine(spritesDir, "atlas.png"));
    }

    public static PixelAssets Current { get; private set; } = null!;

    public static TextCatalog? Texts { get; private set; }

    public Palette Palette { get; }

    public PixelFont BodyFont { get; }

    public PixelFont TitleFont { get; }

    public SpriteAtlas Atlas { get; }

    /// <summary>Подключает дизайн-систему к ресурсам приложения.</summary>
    public static void Install(ResourceDictionary resources, string assetsDir, TextCatalog? texts)
    {
        var assets = new PixelAssets(assetsDir);
        Current = assets;
        Texts = texts;

        foreach (var color in assets.Palette.Colors)
        {
            var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            resources[$"Brush.{color.Name}"] = brush;
        }

        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ADHDWindows;component/Design/Pixel.xaml"),
        });
    }

    public PixelFont Font(PixelFontRole role) => role == PixelFontRole.Title ? TitleFont : BodyFont;

    public Brush Brush(string colorName) => (Brush)Application.Current.FindResource($"Brush.{colorName}");

    public AtlasSprite? Find(string? name) =>
        name is not null && _sprites.TryGetValue(name, out var sprite) ? sprite : null;

    public ImageSource Frame(AtlasSprite sprite, int frame)
    {
        var key = (sprite.Name, frame);
        if (!_frames.TryGetValue(key, out var image))
        {
            image = Crop(sprite.X + (frame * sprite.Width), sprite.Y, sprite.Width, sprite.Height);
            _frames[key] = image;
        }

        return image;
    }

    /// <summary>Девять частей рамки: слева направо, сверху вниз.</summary>
    public ImageSource[] Slices(AtlasSprite sprite)
    {
        if (_slices.TryGetValue(sprite.Name, out var pieces))
        {
            return pieces;
        }

        var edge = sprite.Slice;
        int[] x = [0, edge, sprite.Width - edge];
        int[] y = [0, edge, sprite.Height - edge];
        int[] width = [edge, sprite.Width - (edge * 2), edge];
        int[] height = [edge, sprite.Height - (edge * 2), edge];

        pieces = new ImageSource[SlicePieces];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                pieces[(row * 3) + column] =
                    Crop(sprite.X + x[column], sprite.Y + y[row], width[column], height[row]);
            }
        }

        _slices[sprite.Name] = pieces;
        return pieces;
    }

    private ImageSource Crop(int x, int y, int width, int height)
    {
        var image = new CroppedBitmap(_atlas, new Int32Rect(x, y, width, height));
        image.Freeze();
        return image;
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder
            .Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
            .Frames[0];
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
