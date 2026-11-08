using System.Text;

namespace ADHDWindows.Modules.Locks.Games;

/// <summary>Узел текстового формата Valve KeyValues: значение или вложенные узлы.</summary>
public sealed class VdfNode
{
    public string? Value { get; init; }

    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode? this[string key] => Children.GetValueOrDefault(key);

    public string? Text(string key) => this[key]?.Value;
}

/// <summary>Читает файлы Steam вида "ключ" "значение" и "ключ" { ... }.</summary>
public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        var position = 0;
        return ReadChildren(text, ref position, nested: false);
    }

    private static VdfNode ReadChildren(string text, ref int position, bool nested)
    {
        var node = new VdfNode();
        while (true)
        {
            var key = NextToken(text, ref position);
            if (key is null || key == "}")
            {
                if (nested && key is null)
                {
                    throw new FormatException("Файл Steam оборван");
                }

                return node;
            }

            var value = NextToken(text, ref position) ?? throw new FormatException($"Нет значения у ключа {key}");
            node.Children[key] = value == "{" ? ReadChildren(text, ref position, nested: true) : new VdfNode { Value = value };
        }
    }

    /// <summary>Возвращает строку без кавычек, "{" или "}". Комментарии // пропускаются.</summary>
    private static string? NextToken(string text, ref int position)
    {
        while (position < text.Length)
        {
            var symbol = text[position];
            if (char.IsWhiteSpace(symbol))
            {
                position++;
            }
            else if (symbol == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n')
                {
                    position++;
                }
            }
            else if (symbol is '{' or '}')
            {
                position++;
                return symbol.ToString();
            }
            else if (symbol == '"')
            {
                return ReadQuoted(text, ref position);
            }
            else
            {
                var start = position;
                while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"'))
                {
                    position++;
                }

                return text[start..position];
            }
        }

        return null;
    }

    private static string ReadQuoted(string text, ref int position)
    {
        position++;
        var builder = new StringBuilder();
        while (position < text.Length && text[position] != '"')
        {
            if (text[position] == '\\' && position + 1 < text.Length)
            {
                position++;
                builder.Append(text[position] switch { 'n' => '\n', 't' => '\t', var other => other });
            }
            else
            {
                builder.Append(text[position]);
            }

            position++;
        }

        if (position >= text.Length)
        {
            throw new FormatException("Незакрытая кавычка в файле Steam");
        }

        position++;
        return builder.ToString();
    }
}
