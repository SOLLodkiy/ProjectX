using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Safety;

/// <summary>Значения совпадают с флагами RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>Сочетание клавиш в виде "Ctrl+Alt+Shift+Q".</summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public const string SettingKey = "safety/hotkey";

    private const int FirstFunctionKey = 0x70;
    private const int LastFunctionNumber = 24;

    public static readonly Hotkey Default = new(
        HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
        'Q');

    /// <summary>Сочетание из настроек. Ошибка в настройке не должна оставить пользователя без аварийной остановки.</summary>
    public static Hotkey FromSettings(IKeyValueStore settings, ILog log)
    {
        var text = settings.Get<string>(SettingKey);
        if (text is null)
        {
            return Default;
        }

        if (TryParse(text, out var hotkey))
        {
            return hotkey;
        }

        log.Warn($"Неверное сочетание клавиш в настройке {SettingKey}: {text}");
        return Default;
    }

    /// <summary>Требует хотя бы один модификатор: иначе клавиша перестанет работать во всех программах.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = Default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = ParseModifier(part);
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (modifiers == HotkeyModifiers.None || ParseKey(parts[^1]) is not { } key)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(VirtualKey >= FirstFunctionKey
            ? $"F{VirtualKey - FirstFunctionKey + 1}"
            : ((char)VirtualKey).ToString());
        return string.Join('+', parts);
    }

    private static HotkeyModifiers ParseModifier(string text) => text.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => HotkeyModifiers.Control,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private static int? ParseKey(string text)
    {
        var upper = text.ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
        {
            return upper[0];
        }

        if (upper.Length > 1 && upper[0] == 'F' && int.TryParse(upper[1..], out var number)
            && number is >= 1 and <= LastFunctionNumber)
        {
            return FirstFunctionKey + number - 1;
        }

        return null;
    }
}
