using System.Text.Json;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Pixels;

/// <summary>Положение спрайта в атласе. Кадры лежат подряд слева направо.</summary>
public sealed record AtlasSprite(string Name, int X, int Y, int Width, int Height, int Slice, int Fps, int Frames);

/// <summary>Описание атласа из assets/sprites/atlas.json.</summary>
public sealed record SpriteAtlas(int Width, int Height, IReadOnlyList<AtlasSprite> Sprites)
{
    public string ToJson() => JsonSerializer.Serialize(this, JsonDefaults.Options);

    public static SpriteAtlas Parse(string json) =>
        JsonSerializer.Deserialize<SpriteAtlas>(json, JsonDefaults.Options)
        ?? throw new FormatException("Пустое описание атласа");
}

/// <summary>Результат сборки: описание атласа и его пиксели в формате ARGB, 0 — прозрачный.</summary>
public sealed record PackedAtlas(SpriteAtlas Atlas, uint[] Pixels);

/// <summary>Укладывает спрайты в атлас рядами. Порядок определён, поэтому результат воспроизводим.</summary>
public static class SpriteAtlasPacker
{
    private const int AtlasWidth = 256;
    private const int Padding = 1;

    public static PackedAtlas Pack(IEnumerable<SpriteDefinition> definitions, Palette palette)
    {
        var sorted = definitions
            .OrderByDescending(sprite => sprite.Height)
            .ThenBy(sprite => sprite.Name, StringComparer.Ordinal)
            .ToList();

        var duplicate = sorted.GroupBy(sprite => sprite.Name).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new FormatException($"Спрайт {duplicate.Key} объявлен дважды");
        }

        var placed = new List<AtlasSprite>();
        int x = 0, y = 0, rowHeight = 0;
        foreach (var sprite in sorted)
        {
            var width = sprite.Width * sprite.Frames.Count;
            if (width > AtlasWidth)
            {
                throw new FormatException($"Спрайт {sprite.Name} шире атласа");
            }

            if (x + width > AtlasWidth)
            {
                x = 0;
                y += rowHeight + Padding;
                rowHeight = 0;
            }

            placed.Add(new AtlasSprite(
                sprite.Name, x, y, sprite.Width, sprite.Height, sprite.Slice, sprite.Fps, sprite.Frames.Count));
            x += width + Padding;
            rowHeight = Math.Max(rowHeight, sprite.Height);
        }

        var height = y + rowHeight;
        var pixels = new uint[AtlasWidth * height];
        for (var index = 0; index < sorted.Count; index++)
        {
            Draw(sorted[index], placed[index], pixels, palette);
        }

        return new PackedAtlas(new SpriteAtlas(AtlasWidth, height, placed), pixels);
    }

    private static void Draw(SpriteDefinition sprite, AtlasSprite place, uint[] pixels, Palette palette)
    {
        for (var frame = 0; frame < sprite.Frames.Count; frame++)
        {
            var rows = sprite.Frames[frame];
            for (var row = 0; row < rows.Count; row++)
            {
                for (var column = 0; column < rows[row].Length; column++)
                {
                    if (palette.TryGetByKey(rows[row][column], out var color))
                    {
                        var targetX = place.X + (frame * sprite.Width) + column;
                        pixels[(place.Y + row) * AtlasWidth + targetX] = color.Argb;
                    }
                }
            }
        }
    }
}
