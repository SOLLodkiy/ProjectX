using System.Globalization;
using System.Text.Json;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Pixels;

/// <summary>Цвет палитры. <see cref="Key"/> — символ этого цвета в текстовых спрайтах.</summary>
public sealed record PaletteColor(string Name, char Key, byte R, byte G, byte B)
{
    public uint Argb => 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
}

/// <summary>Единая ограниченная палитра из assets/palette.json.</summary>
public sealed class Palette
{
    /// <summary>Символ прозрачного пикселя в текстовых спрайтах.</summary>
    public const char TransparentKey = '.';

    private readonly Dictionary<char, PaletteColor> _byKey = [];
    private readonly Dictionary<string, PaletteColor> _byName = new(StringComparer.Ordinal);

    public Palette(IEnumerable<PaletteColor> colors)
    {
        Colors = colors.ToList();
        foreach (var color in Colors)
        {
            if (color.Key == TransparentKey)
            {
                throw new FormatException($"Символ '{TransparentKey}' зарезервирован за прозрачностью: {color.Name}");
            }

            if (!_byKey.TryAdd(color.Key, color))
            {
                throw new FormatException($"Символ '{color.Key}' повторяется в палитре");
            }

            if (!_byName.TryAdd(color.Name, color))
            {
                throw new FormatException($"Цвет {color.Name} повторяется в палитре");
            }
        }
    }

    public IReadOnlyList<PaletteColor> Colors { get; }

    public PaletteColor this[string name] => _byName[name];

    public bool TryGetByKey(char key, out PaletteColor color) => _byKey.TryGetValue(key, out color!);

    public static Palette Parse(string json)
    {
        var file = JsonSerializer.Deserialize<PaletteFile>(json, JsonDefaults.Options)
            ?? throw new FormatException("Пустой файл палитры");
        return new Palette(file.Colors.Select(entry =>
        {
            if (entry.Key.Length != 1)
            {
                throw new FormatException($"Символ цвета {entry.Name} должен быть один");
            }

            var hex = entry.Hex.TrimStart('#');
            if (hex.Length != 6)
            {
                throw new FormatException($"Цвет {entry.Name} должен иметь вид #RRGGBB");
            }

            return new PaletteColor(entry.Name, entry.Key[0], Channel(hex, 0), Channel(hex, 2), Channel(hex, 4));
        }));
    }

    private static byte Channel(string hex, int start) =>
        byte.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private sealed record PaletteFile(List<PaletteEntry> Colors);

    private sealed record PaletteEntry(string Name, string Key, string Hex);
}
