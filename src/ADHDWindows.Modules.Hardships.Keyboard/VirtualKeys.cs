using ADHDWindows.Core.Display;
using ADHDWindows.Core.Pixels;
using ADHDWindows.Modules.Hardships.Screen;

namespace ADHDWindows.Modules.Hardships.Keyboard;

/// <summary>Что делает клавиша виртуальной клавиатуры.</summary>
public enum VirtualKeyKind
{
    Character,
    Shift,
    Language,
    Space,
    Backspace,
    Enter,
}

/// <summary>Клавиша: место в точках арта и действие.</summary>
public sealed record VirtualKey(VirtualKeyKind Kind, int Row, int Column, ScreenRect Bounds);

/// <summary>Нажатие клавиши: что напечатать или какую клавишу нажать.</summary>
public sealed record VirtualPress(char? Character, int? Press);

/// <summary>
/// Виртуальная клавиатура: ряды символов из данных, Shift, переключение раскладки, пробел, Backspace и Enter.
/// Shift действует на один символ. Все размеры — в точках арта. Потоками управляет владелец.
/// </summary>
public sealed class VirtualKeyboard
{
    public const int KeySize = 14;
    public const int Gap = 1;
    public const int Padding = 4;
    public const int TitleHeight = 13;

    private readonly IReadOnlyList<KeyboardLayoutData> _layouts;
    private readonly List<VirtualKey> _keys = [];

    public VirtualKeyboard(IReadOnlyList<KeyboardLayoutData> layouts)
    {
        _layouts = layouts.Count > 0 ? layouts : [.. KeyboardData.Default.Layouts.Values];
        var columns = _layouts.Max(layout => layout.Rows.Max(row => row.Length));
        Width = (Padding * 2) + (columns * (KeySize + Gap)) - Gap;
        var rows = _layouts.Max(layout => layout.Rows.Count);
        for (var row = 0; row < rows; row++)
        {
            var count = _layouts.Max(layout => row < layout.Rows.Count ? layout.Rows[row].Length : 0);

            // Короткие ряды сдвинуты вправо, как на настоящей клавиатуре.
            var shift = (columns - count) * (KeySize + Gap) / 2;
            for (var column = 0; column < count; column++)
            {
                _keys.Add(new VirtualKey(VirtualKeyKind.Character, row, column, new ScreenRect(Padding + shift + (column * (KeySize + Gap)), RowTop(row), KeySize, KeySize)));
            }
        }

        // Нижний ряд: Shift, раскладка, пробел, Backspace, Enter. Пробел занимает всё оставшееся место.
        var top = RowTop(rows);
        var inner = Width - (Padding * 2);
        var wide = (KeySize * 2) + Gap;
        var space = inner - (wide * 4) - (Gap * 4);
        var x = Padding;
        foreach (var (kind, width) in new[] { (VirtualKeyKind.Shift, wide), (VirtualKeyKind.Language, wide), (VirtualKeyKind.Space, space), (VirtualKeyKind.Backspace, wide), (VirtualKeyKind.Enter, wide) })
        {
            _keys.Add(new VirtualKey(kind, rows, 0, new ScreenRect(x, top, width, KeySize)));
            x += width + Gap;
        }

        Height = top + KeySize + Padding;
    }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<VirtualKey> Keys => _keys;

    public KeyboardLayoutData Layout => _layouts[LayoutIndex];

    public int LayoutIndex { get; private set; }

    public bool Shifted { get; private set; }

    /// <summary>Клавиша под точкой арта или null.</summary>
    public VirtualKey? KeyAt(int x, int y) => _keys.FirstOrDefault(key => key.Bounds.Contains(new ScreenPoint(x, y)));

    /// <summary>Попадает ли точка в полосу заголовка, за которую клавиатуру тянут.</summary>
    public bool InTitle(int x, int y) => x >= 0 && x < Width && y >= 0 && y < TitleHeight;

    /// <summary>Надпись на клавише в текущей раскладке и регистре.</summary>
    public string Label(VirtualKey key) => key.Kind switch
    {
        VirtualKeyKind.Character => Character(key)?.ToString() ?? string.Empty,
        VirtualKeyKind.Shift => "↑",
        VirtualKeyKind.Language => _layouts[(LayoutIndex + 1) % _layouts.Count].Name,
        VirtualKeyKind.Space => string.Empty,
        VirtualKeyKind.Backspace => "←",
        VirtualKeyKind.Enter => "Ввод",
        _ => string.Empty,
    };

    /// <summary>Нажать клавишу. Возвращает, что ввести, или null, если клавиша только меняет состояние клавиатуры.</summary>
    public VirtualPress? Press(VirtualKey key)
    {
        switch (key.Kind)
        {
            case VirtualKeyKind.Shift:
                Shifted = !Shifted;
                return null;
            case VirtualKeyKind.Language:
                LayoutIndex = (LayoutIndex + 1) % _layouts.Count;
                return null;
            case VirtualKeyKind.Space:
                return new VirtualPress(' ', null);
            case VirtualKeyKind.Backspace:
                return new VirtualPress(null, Platform.KeyboardInput.Backspace);
            case VirtualKeyKind.Enter:
                return new VirtualPress(null, Platform.KeyboardInput.Enter);
            default:
                if (Character(key) is not { } character)
                {
                    return null;
                }

                Shifted = false;
                return new VirtualPress(character, null);
        }
    }

    private char? Character(VirtualKey key)
    {
        var rows = Shifted ? Layout.Shifted : Layout.Rows;
        return key.Row < rows.Count && key.Column < rows[key.Row].Length ? rows[key.Row][key.Column] : null;
    }

    private static int RowTop(int row) => TitleHeight + Padding + (row * (KeySize + Gap));
}

/// <summary>Пиксельная картинка виртуальной клавиатуры в стиле окон программы.</summary>
public static class VirtualKeyboardArt
{
    public static Picture Render(VirtualKeyboard keyboard, PixelFont? font, string title, VirtualKey? pressed)
    {
        var picture = new Picture(keyboard.Width, keyboard.Height);
        picture.Fill(0, 0, picture.Width, picture.Height, Colors.Ink);
        picture.Fill(1, 1, picture.Width - 2, picture.Height - 2, Colors.Panel);
        picture.Fill(1, 1, picture.Width - 2, VirtualKeyboard.TitleHeight - 1, Colors.PanelHi);
        picture.Fill(1, VirtualKeyboard.TitleHeight, picture.Width - 2, 1, Colors.Line);

        // Полоска-ручка в заголовке: за неё тянут.
        for (var x = picture.Width - 22; x < picture.Width - 5; x += 3)
        {
            picture.Fill(x, 4, 2, 2, Colors.Mist);
            picture.Fill(x, 8, 2, 2, Colors.Mist);
        }

        if (font is not null)
        {
            OverlayFont.Draw(picture, font, title, 5, 1, Colors.Text);
        }

        foreach (var key in keyboard.Keys)
        {
            var down = pressed == key || (key.Kind == VirtualKeyKind.Shift && keyboard.Shifted);
            var bounds = key.Bounds;
            picture.Fill(bounds.Left, bounds.Top, bounds.Width, bounds.Height, Colors.Ink);
            picture.Fill(bounds.Left + 1, bounds.Top + 1, bounds.Width - 2, bounds.Height - 2, down ? Colors.Gold : Colors.Line);

            // Объём: светлая кромка сверху у отжатой клавиши.
            if (!down)
            {
                picture.Fill(bounds.Left + 1, bounds.Top + 1, bounds.Width - 2, 1, Colors.Mist);
            }

            var label = keyboard.Label(key);
            if (font is null || label.Length == 0)
            {
                continue;
            }

            var width = font.MeasureWidth(label);
            var x = bounds.Left + ((bounds.Width - width) / 2) + 1;
            var y = bounds.Top + ((bounds.Height - font.LineHeight) / 2) + (down ? 1 : 0);
            OverlayFont.Draw(picture, font, label, x, y, down ? Colors.Ink : Colors.Text);
        }

        return picture;
    }
}
