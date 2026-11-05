using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using ADHDWindows.Core.Pixels;

namespace ADHDWindows.AssetTool;

/// <summary>
/// Переводит TTF с контурами по пиксельной сетке в растровый шрифт: проверяет центр каждого пикселя
/// на попадание в контур. Поэтому результат не зависит от сглаживания и подсказок шрифта.
/// </summary>
internal static class FontCommand
{
    private const string PreviewText = """
        Съешь же ещё этих мягких французских булок, да выпей чаю.
        СЪЕШЬ ЖЕ ЕЩЁ ЭТИХ МЯГКИХ ФРАНЦУЗСКИХ БУЛОК, ДА ВЫПЕЙ ЧАЮ!
        Монетки: 1 234 567 890  +15%  «Магазин» — №7 (x2) 12:45
        The quick brown fox jumps over the lazy dog?
        """;

    private const string ExtraCharacters = "«»—–…№•×→←↑↓";

    public static int Run(string ttfPath, int size, string name, string outputPath, string? previewPath)
    {
        var typeface = new GlyphTypeface(new Uri(Path.GetFullPath(ttfPath)));
        var codes = Enumerable.Range(0x20, 0x5F)
            .Concat(Enumerable.Range(0x400, 0x60))
            .Concat(ExtraCharacters.Select(character => (int)character));

        var raw = new List<RawGlyph>();
        foreach (var code in codes)
        {
            if (typeface.CharacterToGlyphMap.TryGetValue(code, out var index))
            {
                raw.Add(Rasterize(typeface, index, code, size));
            }
        }

        var solid = raw.Where(glyph => glyph.Pixels.Count > 0).ToList();
        var top = solid.Min(glyph => glyph.Pixels.Min(pixel => pixel.Y));
        var bottom = solid.Max(glyph => glyph.Pixels.Max(pixel => pixel.Y));
        var font = new PixelFont(name, bottom - top + 1, raw.Select(glyph => glyph.ToGlyph(top)).ToList());

        File.WriteAllText(outputPath, font.ToJson() + "\n");
        Console.WriteLine($"{name}: символов {font.Glyphs.Count}, высота строки {font.LineHeight}");
        if (previewPath is not null)
        {
            File.WriteAllBytes(previewPath, Preview(font));
        }

        return 0;
    }

    private static RawGlyph Rasterize(GlyphTypeface typeface, ushort index, int code, int size)
    {
        var geometry = typeface.GetGlyphOutline(index, size, size);
        var advance = (int)Math.Round(typeface.AdvanceWidths[index] * size, MidpointRounding.AwayFromZero);
        var pixels = new List<(int X, int Y)>();
        var bounds = geometry.Bounds;
        if (!bounds.IsEmpty)
        {
            for (var y = (int)Math.Floor(bounds.Top); y < Math.Ceiling(bounds.Bottom); y++)
            {
                for (var x = (int)Math.Floor(bounds.Left); x < Math.Ceiling(bounds.Right); x++)
                {
                    if (geometry.FillContains(new Point(x + 0.5, y + 0.5)))
                    {
                        pixels.Add((x, y));
                    }
                }
            }
        }

        return new RawGlyph(code, advance, pixels);
    }

    private static byte[] Preview(PixelFont font)
    {
        const int Scale = 2;
        const int Margin = 4;
        var lineStep = font.LineHeight + 3;
        var lines = PreviewText.ReplaceLineEndings("\n").Split('\n');
        var width = (lines.Max(line => font.MeasureWidth(line)) + (Margin * 2)) * Scale;
        var height = ((lines.Length * lineStep) + (Margin * 2)) * Scale;
        var pixels = new uint[width * height];
        Array.Fill(pixels, 0xFF2B2647u);

        for (var row = 0; row < lines.Length; row++)
        {
            var penX = Margin;
            var penY = Margin + (row * lineStep);
            foreach (var rune in lines[row].EnumerateRunes())
            {
                var glyph = font.Glyph(rune.Value);
                DrawGlyph(glyph, penX, penY, pixels, width, Scale);
                penX += glyph.Advance;
            }
        }

        return Png.Encode(pixels, width, height);
    }

    private static void DrawGlyph(PixelGlyph glyph, int penX, int penY, uint[] pixels, int width, int scale)
    {
        for (var y = 0; y < glyph.Rows.Count; y++)
        {
            for (var x = 0; x < glyph.Rows[y].Length; x++)
            {
                if (glyph.Rows[y][x] != '#')
                {
                    continue;
                }

                var left = (penX + glyph.OffsetX + x) * scale;
                var top = (penY + glyph.OffsetY + y) * scale;
                for (var sy = 0; sy < scale; sy++)
                {
                    for (var sx = 0; sx < scale; sx++)
                    {
                        pixels[((top + sy) * width) + left + sx] = 0xFFF4EFE0u;
                    }
                }
            }
        }
    }

    private sealed record RawGlyph(int Code, int Advance, List<(int X, int Y)> Pixels)
    {
        public PixelGlyph ToGlyph(int top)
        {
            if (Pixels.Count == 0)
            {
                return new PixelGlyph(Code, Advance, 0, 0, []);
            }

            var left = Pixels.Min(pixel => pixel.X);
            var right = Pixels.Max(pixel => pixel.X);
            var first = Pixels.Min(pixel => pixel.Y);
            var last = Pixels.Max(pixel => pixel.Y);
            var rows = new List<string>();
            for (var y = first; y <= last; y++)
            {
                var row = new StringBuilder();
                for (var x = left; x <= right; x++)
                {
                    row.Append(Pixels.Contains((x, y)) ? '#' : '.');
                }

                rows.Add(row.ToString());
            }

            return new PixelGlyph(Code, Advance, left, first - top, rows);
        }
    }
}
