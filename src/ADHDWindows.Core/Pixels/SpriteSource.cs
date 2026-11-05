using System.Globalization;

namespace ADHDWindows.Core.Pixels;

/// <summary>
/// Спрайт из текстового источника. <see cref="Frames"/> — кадры, каждый кадр — строки символов палитры.
/// <see cref="Slice"/> больше нуля означает рамку: края этой ширины не растягиваются.
/// </summary>
public sealed record SpriteDefinition(
    string Name,
    int Width,
    int Height,
    int Slice,
    int Fps,
    IReadOnlyList<IReadOnlyList<string>> Frames);

/// <summary>
/// Читает текстовые спрайты. Формат файла:
/// <code>
/// # комментарий
/// sprite coin 16 16 [slice=3] [fps=8]
/// ....строки кадра....
/// frame
/// ....строки следующего кадра....
/// </code>
/// </summary>
public static class SpriteSource
{
    public static IReadOnlyList<SpriteDefinition> Parse(string text, Palette palette, string fileName = "")
    {
        var sprites = new List<SpriteDefinition>();
        Builder? current = null;
        var lineNumber = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("sprite ", StringComparison.Ordinal))
            {
                Finish();
                current = Builder.FromHeader(line, Error);
            }
            else if (line == "frame")
            {
                if (current is null)
                {
                    throw Error("frame вне спрайта");
                }

                current.NextFrame(Error);
            }
            else
            {
                if (current is null)
                {
                    throw Error("строка пикселей вне спрайта");
                }

                current.AddRow(line, palette, Error);
            }
        }

        Finish();
        return sprites;

        void Finish()
        {
            if (current is not null)
            {
                sprites.Add(current.Build(Error));
                current = null;
            }
        }

        FormatException Error(string message) => new($"{fileName}:{lineNumber}: {message}");
    }

    private sealed class Builder(string name, int width, int height, int slice, int fps)
    {
        private readonly List<IReadOnlyList<string>> _frames = [];
        private List<string> _rows = [];

        public static Builder FromHeader(string line, Func<string, FormatException> error)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4
                || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(parts[3], CultureInfo.InvariantCulture, out var height)
                || width <= 0 || height <= 0)
            {
                throw error("ожидается: sprite <имя> <ширина> <высота> [slice=N] [fps=N]");
            }

            var slice = 0;
            var fps = 0;
            foreach (var option in parts.Skip(4))
            {
                var pair = option.Split('=');
                if (pair.Length != 2 || !int.TryParse(pair[1], CultureInfo.InvariantCulture, out var value) || value < 0)
                {
                    throw error($"неверный параметр {option}");
                }

                switch (pair[0])
                {
                    case "slice":
                        slice = value;
                        break;
                    case "fps":
                        fps = value;
                        break;
                    default:
                        throw error($"неизвестный параметр {pair[0]}");
                }
            }

            if (slice * 2 >= width || slice * 2 >= height)
            {
                throw error("края рамки не оставляют растягиваемой середины");
            }

            return new Builder(parts[1], width, height, slice, fps);
        }

        public void AddRow(string row, Palette palette, Func<string, FormatException> error)
        {
            if (row.Length != width)
            {
                throw error($"{name}: ширина строки {row.Length}, ожидается {width}");
            }

            foreach (var key in row)
            {
                if (key != Palette.TransparentKey && !palette.TryGetByKey(key, out _))
                {
                    throw error($"{name}: символа '{key}' нет в палитре");
                }
            }

            if (_rows.Count == height)
            {
                throw error($"{name}: строк больше, чем {height}");
            }

            _rows.Add(row);
        }

        public void NextFrame(Func<string, FormatException> error)
        {
            CloseFrame(error);
            _rows = [];
        }

        public SpriteDefinition Build(Func<string, FormatException> error)
        {
            CloseFrame(error);
            return new SpriteDefinition(name, width, height, slice, fps, _frames);
        }

        private void CloseFrame(Func<string, FormatException> error)
        {
            if (_rows.Count != height)
            {
                throw error($"{name}: строк {_rows.Count}, ожидается {height}");
            }

            _frames.Add(_rows);
        }
    }
}
