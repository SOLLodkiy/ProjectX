namespace ADHDWindows.Modules.Hardships.Basic;

/// <summary>Пиксельные курсоры: прозрачный и стрелка с грязью. Рисуются с целым увеличением, без сглаживания.</summary>
public static class CursorArt
{
    private const uint Black = 0xFF14101E;
    private const uint White = 0xFFF4F4F4;

    // Цвета грязи из палитры: тёмная и светлая земля.
    private static readonly uint[] DirtColors = [0xFF5A3A22, 0xFF8A5A2E, 0xFF3E2716];

    // Классическая стрелка 12×19: B — контур, W — заливка.
    private static readonly string[] Arrow =
    [
        "B",
        "BB",
        "BWB",
        "BWWB",
        "BWWWB",
        "BWWWWB",
        "BWWWWWB",
        "BWWWWWWB",
        "BWWWWWWWB",
        "BWWWWWWWWB",
        "BWWWWWWWWWB",
        "BWWWWWWBBBBB",
        "BWWWBWWB",
        "BWWBBWWB",
        "BWB  BWWB",
        "BB   BWWB",
        "B     BWWB",
        "      BWWB",
        "       BB",
    ];

    /// <summary>Полностью прозрачный курсор.</summary>
    public static uint[] Blank(int size) => new uint[size * size];

    /// <summary>
    /// Стрелка с грязью: <paramref name="dirt"/> от 0 до 1 — доля испачканных точек. Грязь растёт на одних и тех же
    /// точках, поэтому с каждым шагом курсор пачкается сильнее, а не меняет узор. На сильной грязи появляются комья вокруг.
    /// </summary>
    public static uint[] DirtyArrow(int size, double dirt)
    {
        var scale = Math.Max(1, size / 32);
        var pixels = new uint[size * size];
        dirt = Math.Clamp(dirt, 0, 1);
        for (var row = 0; row < Arrow.Length; row++)
        {
            for (var column = 0; column < Arrow[row].Length; column++)
            {
                var color = Arrow[row][column] switch
                {
                    'B' => Black,
                    'W' => White,
                    _ => 0u,
                };
                if (color == White && Noise(column, row) < dirt * 0.85)
                {
                    color = DirtColors[(int)(Noise(row, column) * DirtColors.Length) % DirtColors.Length];
                }

                Fill(pixels, size, scale, column, row, color);
            }
        }

        // Комья прилипают снаружи у контура.
        if (dirt > 0.4)
        {
            for (var row = 1; row < Arrow.Length + 1; row++)
            {
                for (var column = 1; column < 14; column++)
                {
                    if (At(column, row) == ' ' && Touches(column, row) && Noise(column + 31, row + 17) < (dirt - 0.4) * 0.6)
                    {
                        Fill(pixels, size, scale, column, row, DirtColors[2]);
                    }
                }
            }
        }

        return pixels;
    }

    /// <summary>Сколько точек картинки видно, для проверок.</summary>
    public static int Visible(uint[] pixels) => pixels.Count(pixel => (pixel >> 24) != 0);

    private static char At(int column, int row) =>
        row >= 0 && row < Arrow.Length && column >= 0 && column < Arrow[row].Length ? Arrow[row][column] : ' ';

    private static bool Touches(int column, int row) =>
        At(column - 1, row) == 'B' || At(column + 1, row) == 'B' || At(column, row - 1) == 'B' || At(column, row + 1) == 'B';

    private static void Fill(uint[] pixels, int size, int scale, int column, int row, uint color)
    {
        if (color == 0)
        {
            return;
        }

        for (var y = 0; y < scale; y++)
        {
            for (var x = 0; x < scale; x++)
            {
                var px = (column * scale) + x;
                var py = (row * scale) + y;
                if (px < size && py < size)
                {
                    pixels[(py * size) + px] = color;
                }
            }
        }
    }

    /// <summary>Постоянный шум от 0 до 1 для точки.</summary>
    private static double Noise(int x, int y)
    {
        var hash = unchecked((uint)((x * 73856093) ^ (y * 19349663) ^ 0x5bd1e995));
        hash ^= hash >> 13;
        hash = unchecked(hash * 0x5bd1e995);
        hash ^= hash >> 15;
        return (hash & 0xFFFF) / 65536.0;
    }
}
