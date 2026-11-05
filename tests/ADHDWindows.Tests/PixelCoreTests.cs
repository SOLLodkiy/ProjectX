using ADHDWindows.Core.Pixels;

namespace ADHDWindows.Tests;

public class PixelCoreTests
{
    private static readonly Palette TestPalette = new(
    [
        new PaletteColor("ink", 'K', 1, 2, 3),
        new PaletteColor("gold", 'Y', 200, 100, 50),
    ]);

    [Theory]
    [InlineData(1.0, 2)]
    [InlineData(1.25, 3)]
    [InlineData(1.5, 3)]
    [InlineData(1.75, 4)]
    [InlineData(2.0, 4)]
    [InlineData(2.5, 5)]
    public void Device_pixels_are_a_whole_number_for_any_monitor_scale(double dpiScale, int expected)
    {
        Assert.Equal(expected, PixelScale.DevicePixels(dpiScale));
        Assert.Equal(expected, PixelScale.LayoutFactor(dpiScale) * dpiScale, 9);
    }

    [Fact]
    public void Device_pixels_are_never_zero() => Assert.Equal(1, PixelScale.DevicePixels(0.1, 1));

    [Fact]
    public void Palette_rejects_repeated_keys()
    {
        var json = """{"colors":[{"name":"a","key":"K","hex":"#000000"},{"name":"b","key":"K","hex":"#FFFFFF"}]}""";
        Assert.Throws<FormatException>(() => Palette.Parse(json));
    }

    [Fact]
    public void Palette_reads_hex_colors()
    {
        var palette = Palette.Parse("""{"colors":[{"name":"a","key":"K","hex":"#102030"}]}""");
        Assert.Equal(0xFF102030u, palette["a"].Argb);
    }

    [Fact]
    public void Sprite_source_reads_frames_and_options()
    {
        var sprites = SpriteSource.Parse(
            """
            # комментарий
            sprite box 3 3 slice=1 fps=4
            KKK
            K.K
            KKK
            frame
            YYY
            Y.Y
            YYY
            """,
            TestPalette);

        var sprite = Assert.Single(sprites);
        Assert.Equal("box", sprite.Name);
        Assert.Equal(1, sprite.Slice);
        Assert.Equal(4, sprite.Fps);
        Assert.Equal(2, sprite.Frames.Count);
    }

    [Theory]
    [InlineData("sprite a 2 2\nKKK\nKK")]
    [InlineData("sprite a 2 2\nKK")]
    [InlineData("sprite a 2 2\nKK\nKK\nKK")]
    [InlineData("sprite a 2 2\nKZ\nKK")]
    [InlineData("KK")]
    [InlineData("sprite a 2 2 slice=1\nKK\nKK")]
    public void Sprite_source_rejects_mistakes(string source) =>
        Assert.Throws<FormatException>(() => SpriteSource.Parse(source, TestPalette));

    [Fact]
    public void Packer_rejects_repeated_names()
    {
        var sprites = SpriteSource.Parse("sprite a 1 1\nK\nsprite a 1 1\nK", TestPalette);
        Assert.Throws<FormatException>(() => SpriteAtlasPacker.Pack(sprites, TestPalette));
    }

    [Fact]
    public void Packer_keeps_sprites_apart()
    {
        var source = string.Concat(Enumerable.Range(0, 40).Select(index =>
            $"sprite s{index} 16 16\n{string.Concat(Enumerable.Repeat(new string('K', 16) + "\n", 16))}"));
        var packed = SpriteAtlasPacker.Pack(SpriteSource.Parse(source, TestPalette), TestPalette);

        var sprites = packed.Atlas.Sprites;
        foreach (var first in sprites)
        {
            Assert.True(first.X + first.Width <= packed.Atlas.Width);
            Assert.True(first.Y + first.Height <= packed.Atlas.Height);
            foreach (var second in sprites.Where(other => other != first))
            {
                var apart = first.X + first.Width < second.X || second.X + second.Width < first.X
                    || first.Y + first.Height < second.Y || second.Y + second.Height < first.Y;
                Assert.True(apart, $"{first.Name} и {second.Name} соприкасаются");
            }
        }
    }

    [Fact]
    public void Text_wraps_by_words_and_cuts_long_words()
    {
        var font = new PixelFont("test", 8,
        [
            new PixelGlyph(' ', 1, 0, 0, []),
            new PixelGlyph('?', 1, 0, 0, []),
            new PixelGlyph('a', 1, 0, 0, []),
            new PixelGlyph('b', 1, 0, 0, []),
        ]);

        Assert.Equal(["aa bb", "a"], font.Wrap("aa bb a", 5));
        Assert.Equal(["aaa", "aaa", "b"], font.Wrap("aaaaaa b", 3));
        Assert.Equal(["aa", "", "bb"], font.Wrap("aa\n\nbb", 5));
        Assert.Equal(["aa bb a"], font.Wrap("aa bb a", 0));
    }

    [Fact]
    public void Unknown_character_uses_question_mark()
    {
        var font = new PixelFont("test", 8, [new PixelGlyph('?', 5, 0, 0, [])]);
        Assert.Equal(10, font.MeasureWidth("Ж€"));
    }
}
