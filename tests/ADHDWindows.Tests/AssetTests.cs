using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ADHDWindows.Core.Pixels;

namespace ADHDWindows.Tests;

public partial class AssetTests
{
    private static readonly string Root = RepositoryRoot.Find();
    private static readonly string Assets = Path.Combine(Root, "assets");

    private static Palette LoadPalette() => Palette.Parse(File.ReadAllText(Path.Combine(Assets, "palette.json")));

    private static PackedAtlas PackSources()
    {
        var palette = LoadPalette();
        var definitions = Directory.GetFiles(Path.Combine(Assets, "src", "sprites"), "*.txt")
            .Order(StringComparer.Ordinal)
            .SelectMany(path => SpriteSource.Parse(File.ReadAllText(path), palette, Path.GetFileName(path)));
        return SpriteAtlasPacker.Pack(definitions, palette);
    }

    [Fact]
    public void Palette_is_limited() => Assert.InRange(LoadPalette().Colors.Count, 8, 32);

    [Fact]
    public void Atlas_description_matches_sprite_sources()
    {
        var saved = SpriteAtlas.Parse(File.ReadAllText(Path.Combine(Assets, "sprites", "atlas.json")));
        Assert.Equal(PackSources().Atlas.ToJson(), saved.ToJson());
    }

    [Fact]
    public void Atlas_image_matches_sprite_sources()
    {
        var packed = PackSources();
        using var stream = File.OpenRead(Path.Combine(Assets, "sprites", "atlas.png"));
        var frame = BitmapDecoder
            .Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
            .Frames[0];
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        var pixels = new uint[bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.Equal(packed.Pixels, pixels);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("title")]
    public void Fonts_cover_russian_text(string name)
    {
        var font = PixelFont.Parse(File.ReadAllText(Path.Combine(Assets, "fonts", $"{name}.json")));
        const string Required = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя"
            + "0123456789+-%.,:;!?()«»—№";

        foreach (var character in Required)
        {
            Assert.True(font.Contains(character), $"В шрифте {name} нет символа {character}");
            Assert.NotEmpty(font.Glyph(character).Rows);
        }
    }

    [Fact]
    public void Font_licenses_are_included()
    {
        Assert.True(File.Exists(Path.Combine(Assets, "fonts", "licenses", "Galmuri-OFL.md")));
        Assert.True(File.Exists(Path.Combine(Assets, "fonts", "licenses", "PressStart2P-OFL.txt")));
    }

    [Fact]
    public void Sprites_named_in_interface_exist()
    {
        var known = PackSources().Atlas.Sprites.Select(sprite => sprite.Name).ToHashSet();
        foreach (var name in Sources("*.xaml", SpriteAttribute()).Concat(Sources("*.cs", SpriteLiteral())))
        {
            Assert.Contains(name, known);
        }
    }

    [Fact]
    public void Texts_named_in_markup_exist()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "data", "texts", "ru.json")));
        foreach (var key in Sources("*.xaml", TextKey()))
        {
            Assert.True(document.RootElement.TryGetProperty(key, out _), $"Нет текста {key}");
        }
    }

    private static IEnumerable<string> Sources(string pattern, Regex regex) =>
        Directory.GetFiles(Path.Combine(Root, "src", "ADHDWindows.App", "Design"), pattern)
            .SelectMany(path => regex.Matches(File.ReadAllText(path)))
            .Select(match => match.Groups[1].Value);

    [GeneratedRegex("""(?:Sprite|Value)="((?:panel|inset|window|title_bar|button|tab|list|scroll|toast|app_icon|coin|close)[a-z_]*)" """)]
    private static partial Regex SpriteAttribute();

    [GeneratedRegex("\"((?:toast|button|tab|list)_?[a-z_]*)\"")]
    private static partial Regex SpriteLiteral();

    [GeneratedRegex(@"\{d:T ([a-zA-Z.]+)\}")]
    private static partial Regex TextKey();
}
