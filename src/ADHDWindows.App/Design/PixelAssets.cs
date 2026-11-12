using System.IO;
using System.Windows;
using System.Windows.Documents;
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

/// <summary>
/// Палитра, шрифты и атлас спрайтов. Загружаются один раз при запуске из папки assets.
/// Тема косметики перекрашивает цвета палитры в атласе и кистях, а косметика добавляет свои спрайты и подменяет
/// спрайт монетки. Всё это работает в потоке интерфейса.
/// </summary>
public sealed class PixelAssets
{
    private const int SlicePieces = 9;

    private readonly Dictionary<string, AtlasSprite> _sprites;
    private readonly Dictionary<(string Name, int Frame), ImageSource> _frames = [];
    private readonly Dictionary<string, ImageSource[]> _slices = [];
    private readonly Dictionary<string, (AtlasSprite Sprite, ImageSource[] Frames)> _extras = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<Brush, string> _brushNames = [];
    private readonly uint[] _basePixels;
    private readonly int _atlasWidth;
    private readonly int _atlasHeight;
    private BitmapSource _atlas;
    private ResourceDictionary? _resources;
    private string _theme = string.Empty;

    private PixelAssets(string assetsDir)
    {
        Palette = Palette.Parse(File.ReadAllText(Path.Combine(assetsDir, "palette.json")));
        BodyFont = PixelFont.Parse(File.ReadAllText(Path.Combine(assetsDir, "fonts", "body.json")));
        TitleFont = PixelFont.Parse(File.ReadAllText(Path.Combine(assetsDir, "fonts", "title.json")));

        var spritesDir = Path.Combine(assetsDir, "sprites");
        Atlas = SpriteAtlas.Parse(File.ReadAllText(Path.Combine(spritesDir, "atlas.json")));
        _sprites = Atlas.Sprites.ToDictionary(sprite => sprite.Name);
        _atlas = LoadBitmap(Path.Combine(spritesDir, "atlas.png"));
        _atlasWidth = _atlas.PixelWidth;
        _atlasHeight = _atlas.PixelHeight;
        _basePixels = new uint[_atlasWidth * _atlasHeight];
        _atlas.CopyPixels(_basePixels, _atlasWidth * 4, 0);
    }

    public static PixelAssets Current { get; private set; } = null!;

    public static TextCatalog? Texts { get; private set; }

    public Palette Palette { get; }

    public PixelFont BodyFont { get; }

    public PixelFont TitleFont { get; }

    public SpriteAtlas Atlas { get; }

    /// <summary>Цвета палитры с учётом темы: по ним проверяется, что окно нарисовано только цветами палитры.</summary>
    public IReadOnlyDictionary<string, Color> ThemeColors { get; private set; } = new Dictionary<string, Color>();

    /// <summary>Подключает дизайн-систему к ресурсам приложения.</summary>
    public static void Install(ResourceDictionary resources, string assetsDir, TextCatalog? texts)
    {
        var assets = new PixelAssets(assetsDir);
        Current = assets;
        Texts = texts;
        assets._resources = resources;
        assets.SetBrushes(null);

        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ADHDWindows;component/Design/Pixel.xaml"),
        });
    }

    public PixelFont Font(PixelFontRole role) => role == PixelFontRole.Title ? TitleFont : BodyFont;

    public Brush Brush(string colorName) => (Brush)Application.Current.FindResource($"Brush.{colorName}");

    public AtlasSprite? Find(string? name)
    {
        if (name is null)
        {
            return null;
        }

        if (_aliases.TryGetValue(name, out var target))
        {
            name = target;
        }

        return _extras.TryGetValue(name, out var extra) ? extra.Sprite
            : _sprites.TryGetValue(name, out var sprite) ? sprite
            : null;
    }

    public ImageSource Frame(AtlasSprite sprite, int frame)
    {
        if (_extras.TryGetValue(sprite.Name, out var extra) && ReferenceEquals(extra.Sprite, sprite))
        {
            return extra.Frames[frame % extra.Frames.Length];
        }

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

    /// <summary>
    /// Добавляет спрайт из картинки косметики: кадры — строки символами палитры. Уже добавленный спрайт заменяется.
    /// </summary>
    public void Register(string name, IReadOnlyList<IReadOnlyList<string>> frames, int fps)
    {
        if (frames.Count == 0)
        {
            return;
        }

        var width = frames.Max(frame => frame.Count == 0 ? 0 : frame.Max(row => row.Length));
        var height = frames.Max(frame => frame.Count);
        var images = frames.Select(frame =>
        {
            var pixels = new uint[width * height];
            for (var y = 0; y < frame.Count; y++)
            {
                for (var x = 0; x < frame[y].Length; x++)
                {
                    if (Palette.TryGetByKey(frame[y][x], out var color))
                    {
                        pixels[(y * width) + x] = color.Argb;
                    }
                }
            }

            return Image(pixels, width, height);
        }).ToArray();
        _extras[name] = (new AtlasSprite(name, -1, -1, width, height, 0, fps, images.Length), images);
    }

    /// <summary>
    /// Добавляет спрайт <paramref name="name"/> — спрайт атласа <paramref name="source"/> с заменой символов палитры
    /// <paramref name="recolor"/>. Так скин монеток повторяет анимацию обычной монетки.
    /// </summary>
    public void RegisterRecolored(string name, string source, IReadOnlyDictionary<char, char> recolor)
    {
        if (!_sprites.TryGetValue(source, out var sprite))
        {
            return;
        }

        var map = new Dictionary<uint, uint>();
        foreach (var (from, to) in recolor)
        {
            if (Palette.TryGetByKey(from, out var original) && Palette.TryGetByKey(to, out var replacement))
            {
                map[original.Argb] = replacement.Argb;
            }
        }

        var images = new ImageSource[sprite.Frames];
        for (var frame = 0; frame < sprite.Frames; frame++)
        {
            var pixels = new uint[sprite.Width * sprite.Height];
            for (var y = 0; y < sprite.Height; y++)
            {
                for (var x = 0; x < sprite.Width; x++)
                {
                    var pixel = _basePixels[((sprite.Y + y) * _atlasWidth) + sprite.X + (frame * sprite.Width) + x];
                    pixels[(y * sprite.Width) + x] = map.TryGetValue(pixel, out var replaced) ? replaced : pixel;
                }
            }

            images[frame] = Image(pixels, sprite.Width, sprite.Height);
        }

        _extras[name] = (sprite with { Name = name, X = -1, Y = -1 }, images);
    }

    /// <summary>
    /// Добавляет значок темы 16×16: маленькое окно в её цветах. <paramref name="colors"/> заменяют цвета палитры по названию.
    /// </summary>
    public void RegisterSwatch(string name, IReadOnlyDictionary<string, string> colors)
    {
        uint Color(string colorName) =>
            colors.TryGetValue(colorName, out var hex) && TryParse(hex, out var argb) ? argb : Palette[colorName].Argb;

        // Рамка line, заголовок panelHi с золотой точкой, тело panel, строки text и mist, обводка ink.
        string[] rows =
        [
            "KKKKKKKKKKKKKKKK",
            "KLLLLLLLLLLLLLLK",
            "KLHHHHHHHHHHHYLK",
            "KLLLLLLLLLLLLLLK",
            "KLPPPPPPPPPPPPLK",
            "KLPTTTTTTTTPPPLK",
            "KLPPPPPPPPPPPPLK",
            "KLPMMMMMMPPPPPLK",
            "KLPPPPPPPPPPPPLK",
            "KLPDDDDDDDDDDPLK",
            "KLPPPPPPPPPPPPLK",
            "KLLLLLLLLLLLLLLK",
            "KKKKKKKKKKKKKKKK",
        ];
        var names = new Dictionary<char, string>
        {
            ['K'] = "ink", ['L'] = "line", ['H'] = "panelHi", ['Y'] = "gold", ['P'] = "panel",
            ['T'] = "text", ['M'] = "mist", ['D'] = "night",
        };
        var pixels = new uint[16 * rows.Length];
        for (var y = 0; y < rows.Length; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                pixels[(y * 16) + x] = Color(names[rows[y][x]]);
            }
        }

        _extras[name] = (new AtlasSprite(name, -1, -1, 16, rows.Length, 0, 0, 1), [Image(pixels, 16, rows.Length)]);
    }

    /// <summary>Спрайт <paramref name="name"/> рисуется как <paramref name="target"/>; null — снова как свой.</summary>
    public void SetAlias(string name, string? target)
    {
        if (target is null)
        {
            _aliases.Remove(name);
        }
        else
        {
            _aliases[name] = target;
        }
    }

    /// <summary>
    /// Перекрашивает интерфейс: цвета палитры по названию заменяются <paramref name="colors"/> (#RRGGBB) в атласе и кистях,
    /// null — исходная палитра. Открытые окна перерисовываются сразу. False — тема уже действует.
    /// </summary>
    public bool ApplyTheme(IReadOnlyDictionary<string, string>? colors)
    {
        var key = colors is null ? string.Empty : string.Join(';', colors.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
        if (key == _theme)
        {
            return false;
        }

        _theme = key;
        var map = new Dictionary<uint, uint>();
        foreach (var color in Palette.Colors)
        {
            if (colors is not null && colors.TryGetValue(color.Name, out var hex) && TryParse(hex, out var replacement))
            {
                map[color.Argb] = replacement;
            }
        }

        var pixels = map.Count == 0 ? _basePixels : _basePixels.Select(pixel => map.TryGetValue(pixel, out var replaced) ? replaced : pixel).ToArray();
        _atlas = Image(pixels, _atlasWidth, _atlasHeight);
        _frames.Clear();
        _slices.Clear();
        var old = new Dictionary<Brush, string>(_brushNames);
        SetBrushes(colors);

        foreach (Window window in Application.Current.Windows)
        {
            Retheme(window, old);
        }

        return true;
    }

    /// <summary>Подставляет новые кисти вместо кистей прежней темы, заданных напрямую, и перерисовывает элементы.</summary>
    internal void Retheme(DependencyObject root, IReadOnlyDictionary<Brush, string>? old = null)
    {
        old ??= _brushNames;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        var seen = new HashSet<DependencyObject>();
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            if (!seen.Add(element))
            {
                continue;
            }

            if (element.ReadLocalValue(TextElement.ForegroundProperty) is Brush brush && old.TryGetValue(brush, out var name))
            {
                element.SetValue(TextElement.ForegroundProperty, Brush(name));
            }

            if (element is UIElement ui)
            {
                ui.InvalidateVisual();
            }

            if (element is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                {
                    stack.Push(VisualTreeHelper.GetChild(element, i));
                }
            }

            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            {
                stack.Push(child);
            }
        }
    }

    private void SetBrushes(IReadOnlyDictionary<string, string>? colors)
    {
        _brushNames.Clear();
        var themeColors = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var color in Palette.Colors)
        {
            var value = Color.FromRgb(color.R, color.G, color.B);
            if (colors is not null && colors.TryGetValue(color.Name, out var hex) && TryParse(hex, out var argb))
            {
                value = Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            }

            var brush = new SolidColorBrush(value);
            brush.Freeze();
            _resources![$"Brush.{color.Name}"] = brush;
            _brushNames[brush] = color.Name;
            themeColors[color.Name] = value;
        }

        ThemeColors = themeColors;
    }

    private static bool TryParse(string hex, out uint argb)
    {
        argb = 0;
        if (hex.Length != 7 || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return false;
        }

        argb = 0xFF000000 | rgb;
        return true;
    }

    private static BitmapSource Image(uint[] pixels, int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
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
