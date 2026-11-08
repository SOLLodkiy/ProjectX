using ADHDWindows.Core.Display;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Pixels;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Screen;

/// <summary>Где появляются муха и реклама.</summary>
public static class OverlayArea
{
    /// <summary>Рабочая область монитора с курсором; в автотестах — тестового монитора.</summary>
    public static ScreenRect WorkArea()
    {
        if (TestScreen.Find() is { } test)
        {
            return test.WorkArea;
        }

        var screens = Desktop.Screens();
        var cursor = CursorControl.Position();
        return (screens.FirstOrDefault(screen => screen.Bounds.Contains(cursor)) ?? screens.FirstOrDefault())?.WorkArea
            ?? new ScreenRect(0, 0, 1280, 720);
    }
}

/// <summary>Пиксельная картинка 0xAARRGGBB, строки сверху вниз.</summary>
public sealed class Picture(int width, int height)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public uint[] Pixels { get; } = new uint[width * height];

    public uint this[int x, int y]
    {
        get => x >= 0 && y >= 0 && x < Width && y < Height ? Pixels[(y * Width) + x] : 0;
        set
        {
            if (x >= 0 && y >= 0 && x < Width && y < Height)
            {
                Pixels[(y * Width) + x] = value;
            }
        }
    }

    public void Fill(int x, int y, int width, int height, uint color)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                this[column, row] = color;
            }
        }
    }

    /// <summary>
    /// Та же картинка с контуром в один пиксель цвета <paramref name="color"/> вокруг видимых точек:
    /// так тёмный спрайт заметен и на тёмном фоне.
    /// </summary>
    public Picture Outlined(uint color)
    {
        var result = new Picture(Width + 2, Height + 2);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                result[x + 1, y + 1] = this[x, y];
            }
        }

        for (var y = 0; y < result.Height; y++)
        {
            for (var x = 0; x < result.Width; x++)
            {
                if (result[x, y] == 0 && (Opaque(x - 2, y - 1) || Opaque(x, y - 1) || Opaque(x - 1, y - 2) || Opaque(x - 1, y)))
                {
                    result[x, y] = color;
                }
            }
        }

        return result;

        bool Opaque(int x, int y) => (this[x, y] >> 24) != 0;
    }

    /// <summary>Картинка из строк: каждый символ — цвет из <paramref name="colors"/>, остальные прозрачны.</summary>
    public static Picture FromRows(IReadOnlyList<string> rows, IReadOnlyDictionary<char, uint> colors)
    {
        var picture = new Picture(rows.Max(row => row.Length), rows.Count);
        for (var y = 0; y < rows.Count; y++)
        {
            for (var x = 0; x < rows[y].Length; x++)
            {
                if (colors.TryGetValue(rows[y][x], out var color))
                {
                    picture[x, y] = color;
                }
            }
        }

        return picture;
    }
}

/// <summary>Цвета из палитры дизайн-системы (assets/palette.json).</summary>
public static class Colors
{
    public const uint Ink = 0xFF14121F;
    public const uint Panel = 0xFF2B2647;
    public const uint PanelHi = 0xFF3D3663;
    public const uint Line = 0xFF5A5289;
    public const uint Mist = 0xFFA89FC4;
    public const uint Text = 0xFFF4EFE0;
    public const uint White = 0xFFFFFFFF;
    public const uint Gold = 0xFFFFCF4A;
    public const uint Amber = 0xFFF09A27;
    public const uint Green = 0xFF5FD068;
    public const uint GreenDark = 0xFF2C8F59;
    public const uint Red = 0xFFFF5A5F;
    public const uint RedDark = 0xFFA82A4E;
    public const uint Violet = 0xFF9B5DE5;
    public const uint Pink = 0xFFFF7AC6;

    /// <summary>Ключи цветов как в assets/src/sprites.</summary>
    public static readonly IReadOnlyDictionary<char, uint> Keys = new Dictionary<char, uint>
    {
        ['K'] = Ink,
        ['P'] = Panel,
        ['M'] = Mist,
        ['T'] = Text,
        ['W'] = White,
        ['Y'] = Gold,
        ['O'] = Amber,
        ['G'] = Green,
        ['g'] = GreenDark,
        ['R'] = Red,
        ['r'] = RedDark,
        ['V'] = Violet,
        ['N'] = Pink,
    };
}

/// <summary>Пиксельный шрифт программы для надписей в оверлеях.</summary>
public static class OverlayFont
{
    private static readonly Lock Gate = new();
    private static PixelFont? _font;
    private static bool _tried;

    /// <summary>Шрифт из assets/fonts/body.json рядом с программой или null, если файла нет.</summary>
    public static PixelFont? Body(ILog log)
    {
        lock (Gate)
        {
            if (_tried)
            {
                return _font;
            }

            _tried = true;
            var path = FindAsset(Path.Combine("assets", "fonts", "body.json"));
            if (path is null)
            {
                log.Warn("Шрифт для оверлеев не найден: надписи не рисуются");
                return null;
            }

            try
            {
                _font = PixelFont.Parse(File.ReadAllText(path));
            }
            catch (Exception exception) when (exception is IOException or FormatException or System.Text.Json.JsonException)
            {
                log.Error("Шрифт для оверлеев не прочитан", exception);
            }

            return _font;
        }
    }

    /// <summary>Рисует строку, верх строки в <paramref name="y"/>. Возвращает ширину.</summary>
    public static int Draw(Picture picture, PixelFont font, string text, int x, int y, uint color)
    {
        var start = x;
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = font.Glyph(rune.Value);
            for (var row = 0; row < glyph.Rows.Count; row++)
            {
                for (var column = 0; column < glyph.Rows[row].Length; column++)
                {
                    if (glyph.Rows[row][column] == '#')
                    {
                        picture[x + glyph.OffsetX + column, y + glyph.OffsetY + row] = color;
                    }
                }
            }

            x += glyph.Advance;
        }

        return x - start;
    }

    // Программа лежит рядом с assets, а проверки запускаются из папки сборки внутри репозитория.
    private static string? FindAsset(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; directory is not null && level < 8; level++, directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, relative);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
