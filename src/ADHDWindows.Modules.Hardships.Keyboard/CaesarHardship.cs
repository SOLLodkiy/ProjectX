using System.Collections.Concurrent;
using System.Globalization;
using ADHDWindows.Core.Hardships;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Hardships.Keyboard;

/// <summary>
/// Шифр Цезаря: буква заменяется соседней справа в своём ряду раскладки, у последней буквы ряда сосед — первая буква
/// того же ряда. Регистр сохраняется, цифры и знаки не меняются.
/// </summary>
public sealed class CaesarCipher
{
    // Коды положения клавиш (scan code) трёх буквенных рядов: Q…], A…', Z…/.
    private static readonly int[][] ScanRows =
    [
        [0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B],
        [0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28],
        [0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35],
    ];

    private readonly Dictionary<char, char> _next = [];
    private readonly Dictionary<(string Language, int ScanCode), char> _letters = [];

    /// <param name="rows">Буквы рядов по языкам: ключ — «ru» или «en».</param>
    public CaesarCipher(IReadOnlyDictionary<string, IReadOnlyList<string>> rows)
    {
        foreach (var (language, letters) in rows)
        {
            for (var row = 0; row < letters.Count; row++)
            {
                var line = letters[row].ToLowerInvariant();
                for (var i = 0; i < line.Length; i++)
                {
                    _next[line[i]] = line[(i + 1) % line.Length];
                    if (row < ScanRows.Length && i < ScanRows[row].Length)
                    {
                        _letters[(language, ScanRows[row][i])] = line[i];
                    }
                }
            }
        }
    }

    /// <summary>Буква после сдвига. Не буква из рядов — без изменений.</summary>
    public char Shift(char letter)
    {
        var lower = char.ToLowerInvariant(letter);
        if (!_next.TryGetValue(lower, out var next))
        {
            return letter;
        }

        return char.IsUpper(letter) ? char.ToUpperInvariant(next) : next;
    }

    /// <summary>
    /// Буква, которую напечатает клавиша в раскладке языка <paramref name="languageId"/> (младшее слово HKL),
    /// или null, если это не буква из рядов или раскладка не русская и не английская.
    /// </summary>
    public char? Letter(int scanCode, int languageId)
    {
        var language = (languageId & 0x3FF) switch
        {
            0x19 => "ru",
            0x09 => "en",
            _ => null,
        };
        return language is not null && _letters.TryGetValue((language, scanCode), out var letter) ? letter : null;
    }

    /// <summary>Что напечатать вместо нажатия: сдвинутая буква в нужном регистре или null — пропустить нажатие.</summary>
    public char? Replace(int scanCode, int languageId, bool upper) =>
        Letter(scanCode, languageId) is { } letter
            ? Shift(upper ? char.ToUpper(letter, CultureInfo.InvariantCulture) : letter)
            : null;
}

/// <summary>
/// Шифр Цезаря: каждая напечатанная буква заменяется соседней справа на раскладке. Нажатие буквы проглатывается,
/// а вместо него печатается сдвинутая буква. Защищённые места, сочетания с Ctrl, Alt и Win и аварийная клавиша
/// не трогаются. Хук снимается вместе с программой, поэтому после сбоя клавиатура сразу обычная.
/// </summary>
public sealed class CaesarHardship : HardshipModule
{
    private readonly HashSet<int> _swallowed = [];
    private readonly Lock _lock = new();
    private BlockingCollection<char>? _output;
    private Thread? _writer;
    private KeyboardFilter? _filter;
    private KeyboardGuard? _guard;
    private CaesarCipher _cipher = new(KeyboardData.Default.Caesar);
    private int _replaced;

    public override HardshipInfo Info { get; } = new("caesar", HardshipLevel.Hard, HardshipResources.Keyboard);

    /// <summary>Шифр, для проверок.</summary>
    internal CaesarCipher Cipher => _cipher;

    /// <summary>Защита, для проверок.</summary>
    internal KeyboardGuard? Guard => _guard;

    /// <summary>Сколько букв заменено.</summary>
    internal int Replaced
    {
        get
        {
            lock (_lock)
            {
                return _replaced;
            }
        }
    }

    /// <summary>Подмена вывода для проверок: что напечатано вместо нажатий.</summary>
    internal Action<char>? Output { get; set; }

    protected override void OnStart() => _cipher = new CaesarCipher(KeyboardData.Load(Context.Data).Caesar);

    protected override void Begin()
    {
        lock (_lock)
        {
            _swallowed.Clear();
            _replaced = 0;
        }

        _guard = new KeyboardGuard(KeyboardData.Load(Context.Data).Protected, Context.Log);
        _output = new BlockingCollection<char>();
        var output = _output;
        _writer = new Thread(() => Write(output)) { IsBackground = true, Name = "Caesar" };
        _writer.Start();
        _filter = new KeyboardFilter(Filter, Context.Log);
        if (!_filter.Start())
        {
            throw new InvalidOperationException("Фильтр клавиатуры для шифра не поставлен");
        }
    }

    protected override void End()
    {
        // Сначала хук: с этого момента клавиатура обычная.
        _filter?.Dispose();
        _filter = null;
        _output?.CompleteAdding();
        _writer?.Join(TimeSpan.FromSeconds(2));
        _writer = null;
        _output?.Dispose();
        _output = null;
        _guard?.Dispose();
        _guard = null;
        lock (_lock)
        {
            Context.Log.Info($"Шифр Цезаря: заменено букв {_replaced}");
        }
    }

    /// <summary>Решение фильтра без хука, для проверок: true — нажатие проглочено.</summary>
    internal bool Filter(KeyStroke stroke) => Decide(stroke, KeyboardFilter.ForegroundLanguage(), KeyboardFilter.ShiftDown() ^ KeyboardFilter.CapsLockOn(), KeyboardFilter.ShortcutModifierDown());

    internal bool Decide(KeyStroke stroke, int language, bool upper, bool shortcut)
    {
        lock (_lock)
        {
            if (!stroke.Down)
            {
                // Отпускание проглоченной клавиши тоже не доходит до программы.
                return _swallowed.Remove(stroke.ScanCode);
            }

            if (shortcut || _guard?.IsProtected != false || stroke.Extended)
            {
                return false;
            }

            if (_cipher.Replace(stroke.ScanCode, language, upper) is not { } letter)
            {
                return false;
            }

            _swallowed.Add(stroke.ScanCode);
            _replaced++;
            if (Output is { } output)
            {
                output(letter);
            }
            else
            {
                _output?.TryAdd(letter);
            }

            return true;
        }
    }

    private void Write(BlockingCollection<char> output)
    {
        try
        {
            foreach (var letter in output.GetConsumingEnumerable())
            {
                try
                {
                    KeyboardInput.Type(letter);
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception)
                {
                    Context.Log.Warn($"Буква не напечатана: {exception.Message}");
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Усложнение снято.
        }
    }
}
