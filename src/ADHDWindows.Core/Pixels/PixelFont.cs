using System.Text.Json;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Pixels;

/// <summary>
/// Символ растрового шрифта. <see cref="Rows"/> — строки пикселей рамки символа в виде «#» и «.»,
/// <see cref="OffsetY"/> считается от верха строки текста.
/// </summary>
public sealed record PixelGlyph(int Code, int Advance, int OffsetX, int OffsetY, IReadOnlyList<string> Rows);

/// <summary>Растровый шрифт из assets/fonts. Все размеры указаны в пикселях арта.</summary>
public sealed class PixelFont
{
    private readonly Dictionary<int, PixelGlyph> _glyphs;

    public PixelFont(string name, int lineHeight, IReadOnlyList<PixelGlyph> glyphs)
    {
        Name = name;
        LineHeight = lineHeight;
        Glyphs = glyphs;
        _glyphs = glyphs.ToDictionary(glyph => glyph.Code);
    }

    public string Name { get; }

    public int LineHeight { get; }

    public IReadOnlyList<PixelGlyph> Glyphs { get; }

    public bool Contains(int code) => _glyphs.ContainsKey(code);

    /// <summary>
    /// Символ текста. Неразрывные и узкие пробелы (разделитель тысяч в русских числах) рисуются обычным пробелом,
    /// вместо остальных отсутствующих в шрифте символов показывается «?».
    /// </summary>
    public PixelGlyph Glyph(int code) =>
        _glyphs.TryGetValue(code, out var glyph) ? glyph
        : code <= char.MaxValue && char.IsWhiteSpace((char)code) && _glyphs.TryGetValue(' ', out var space) ? space
        : _glyphs['?'];

    public int MeasureWidth(ReadOnlySpan<char> text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += Glyph(rune.Value).Advance;
        }

        return width;
    }

    /// <summary>
    /// Разбивает текст на строки не шире <paramref name="maxWidth"/>. Перенос идёт по пробелам,
    /// слишком длинное слово режется по символам. Ноль или меньше означает без переноса.
    /// </summary>
    public IReadOnlyList<string> Wrap(string text, int maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (maxWidth <= 0)
            {
                lines.Add(paragraph);
                continue;
            }

            WrapParagraph(paragraph, maxWidth, lines);
        }

        return lines;
    }

    public string ToJson() => JsonSerializer.Serialize(new FontFile(Name, LineHeight, Glyphs), JsonDefaults.Options);

    public static PixelFont Parse(string json)
    {
        var file = JsonSerializer.Deserialize<FontFile>(json, JsonDefaults.Options)
            ?? throw new FormatException("Пустой файл шрифта");
        return new PixelFont(file.Name, file.LineHeight, file.Glyphs);
    }

    private void WrapParagraph(string paragraph, int maxWidth, List<string> lines)
    {
        var line = "";
        foreach (var word in paragraph.Split(' '))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (MeasureWidth(candidate) <= maxWidth)
            {
                line = candidate;
                continue;
            }

            if (line.Length > 0)
            {
                lines.Add(line);
            }

            line = word;
            while (MeasureWidth(line) > maxWidth && line.Length > 1)
            {
                var fit = 1;
                while (fit < line.Length && MeasureWidth(line.AsSpan(0, fit + 1)) <= maxWidth)
                {
                    fit++;
                }

                lines.Add(line[..fit]);
                line = line[fit..];
            }
        }

        lines.Add(line);
    }

    private sealed record FontFile(string Name, int LineHeight, IReadOnlyList<PixelGlyph> Glyphs);
}
