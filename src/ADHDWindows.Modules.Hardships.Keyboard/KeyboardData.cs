using ADHDWindows.Core.Data;

namespace ADHDWindows.Modules.Hardships.Keyboard;

/// <summary>Данные усложнений клавиатуры из data/hardships/keyboard.json.</summary>
public sealed record KeyboardData(ProtectedPlaces Protected, IReadOnlyDictionary<string, IReadOnlyList<string>> Caesar, IReadOnlyDictionary<string, KeyboardLayoutData> Layouts)
{
    public const string DataKey = "hardships/keyboard";

    /// <summary>Данные из файла или встроенные, если файла нет: защита клавиатуры работает всегда.</summary>
    public static KeyboardData Load(GameData data) =>
        data.TryGet<KeyboardData>(DataKey, out var value) && value is { Protected: not null, Caesar: not null, Layouts: not null }
            ? value
            : Default;

    /// <summary>Минимальные данные на случай повреждённого файла.</summary>
    public static KeyboardData Default { get; } = new(
        new ProtectedPlaces(
            ["claude", "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "consent", "LogonUI", "LockApp", "CredentialUIBroker"],
            ["ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "Credential Dialog Xaml Host"],
            ["Anthropic*"]),
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["ru"] = ["йцукенгшщзхъ", "фывапролджэ", "ячсмитьбю"],
            ["en"] = ["qwertyuiop", "asdfghjkl", "zxcvbnm"],
        },
        new Dictionary<string, KeyboardLayoutData>
        {
            ["ru"] = new("РУ", ["1234567890-=", "йцукенгшщзхъ", "фывапролджэ", "ячсмитьбю."], ["!\"№;%:?*()_+", "ЙЦУКЕНГШЩЗХЪ", "ФЫВАПРОЛДЖЭ", "ЯЧСМИТЬБЮ,"]),
            ["en"] = new("EN", ["1234567890-=", "qwertyuiop[]", "asdfghjkl;'", "zxcvbnm,./"], ["!@#$%^&*()_+", "QWERTYUIOP{}", "ASDFGHJKL:\"", "ZXCVBNM<>?"]),
        });
}

/// <summary>Где клавиатура всегда работает как обычно: процессы, классы окон и издатели (маска со звёздочкой в конце).</summary>
public sealed record ProtectedPlaces(IReadOnlyList<string> Processes, IReadOnlyList<string> Classes, IReadOnlyList<string> Companies);

/// <summary>Раскладка виртуальной клавиатуры: ряды символов без Shift и с Shift.</summary>
public sealed record KeyboardLayoutData(string Name, IReadOnlyList<string> Rows, IReadOnlyList<string> Shifted);
