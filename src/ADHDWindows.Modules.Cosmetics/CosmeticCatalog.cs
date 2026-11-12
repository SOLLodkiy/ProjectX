using System.Text.Json;
using ADHDWindows.Core.Cosmetics;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Modules.Cosmetics;

/// <summary>
/// Предметы косметики из data/cosmetics. Файл задаёт место предмета: cursors, themes, coins, pets; в effects.json
/// место указывает поле slot (trail, click, purchase). Новый предмет добавляется записью в данных и строками текста.
/// Ошибочная запись пропускается с предупреждением и не мешает остальным.
/// </summary>
public static class CosmeticCatalog
{
    public const string Folder = "cosmetics";

    /// <summary>Наибольший размер картинки курсора: система показывает курсор 32×32, арт увеличивается вдвое.</summary>
    public const int MaxCursorArt = 16;

    private static readonly (string File, CosmeticSlot? Slot)[] Files =
    [
        ("cursors", CosmeticSlot.Cursor),
        ("themes", CosmeticSlot.Theme),
        ("coins", CosmeticSlot.Coin),
        ("effects", null),
        ("pets", CosmeticSlot.Pet),
    ];

    /// <summary>
    /// Читает все файлы. <paramref name="paletteKeys"/> — символы палитры для проверки картинок, <paramref name="paletteNames"/> —
    /// названия цветов для проверки тем; null — не проверять.
    /// </summary>
    public static IReadOnlyList<CosmeticItem> Load(GameData data, ILog log, ISet<char>? paletteKeys = null, ISet<string>? paletteNames = null)
    {
        var items = new List<CosmeticItem>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, slot) in Files)
        {
            var key = $"{Folder}/{file}";
            if (!data.TryGet<JsonElement>(key, out var root) || root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                log.Warn($"Нет данных косметики {key}");
                continue;
            }

            foreach (var element in list.EnumerateArray())
            {
                var item = Parse(element, slot, key, log, paletteKeys, paletteNames);
                if (item is null)
                {
                    continue;
                }

                if (!codes.Add(item.Code))
                {
                    log.Warn($"Косметика {item.Code} повторяется в {key}, пропущена");
                    continue;
                }

                items.Add(item);
            }
        }

        return items;
    }

    private static CosmeticItem? Parse(JsonElement element, CosmeticSlot? fileSlot, string key, ILog log, ISet<char>? keys, ISet<string>? names)
    {
        var code = Text(element, "code");
        if (string.IsNullOrWhiteSpace(code))
        {
            log.Warn($"Косметика без кода в {key}, пропущена");
            return null;
        }

        var slot = fileSlot;
        if (slot is null)
        {
            slot = Text(element, "slot") switch
            {
                "trail" => CosmeticSlot.Trail,
                "click" => CosmeticSlot.Click,
                "purchase" => CosmeticSlot.Purchase,
                _ => null,
            };
            if (slot is null)
            {
                log.Warn($"Косметика {code}: неизвестное место {Text(element, "slot")}, пропущена");
                return null;
            }
        }

        var rarity = Text(element, "rarity") switch
        {
            null or "common" => CosmeticRarity.Common,
            "rare" => CosmeticRarity.Rare,
            "epic" => CosmeticRarity.Epic,
            "legendary" => CosmeticRarity.Legendary,
            var other => Fail(log, $"Косметика {code}: неизвестная редкость {other}"),
        };

        var animations = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.Ordinal);
        if (element.TryGetProperty("art", out var art))
        {
            animations[CosmeticItem.DefaultAnimation] = [Rows(art)];
        }

        if (element.TryGetProperty("frames", out var frames) && frames.ValueKind == JsonValueKind.Array)
        {
            animations[CosmeticItem.DefaultAnimation] = frames.EnumerateArray().Select(Rows).ToList();
        }

        if (element.TryGetProperty("animations", out var named) && named.ValueKind == JsonValueKind.Object)
        {
            foreach (var animation in named.EnumerateObject())
            {
                if (animation.Value.ValueKind == JsonValueKind.Array)
                {
                    animations[animation.Name] = animation.Value.EnumerateArray().Select(Rows).ToList();
                }
            }
        }

        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty("colors", out var colorList) && colorList.ValueKind == JsonValueKind.Object)
        {
            foreach (var color in colorList.EnumerateObject())
            {
                colors[color.Name] = color.Value.GetString() ?? string.Empty;
            }
        }

        var recolor = new Dictionary<char, char>();
        if (element.TryGetProperty("recolor", out var recolorList) && recolorList.ValueKind == JsonValueKind.Object)
        {
            foreach (var pair in recolorList.EnumerateObject())
            {
                var to = pair.Value.GetString();
                if (pair.Name.Length == 1 && to is { Length: 1 })
                {
                    recolor[pair.Name[0]] = to[0];
                }
            }
        }

        var hot = element.TryGetProperty("hot", out var hotList) && hotList.ValueKind == JsonValueKind.Array && hotList.GetArrayLength() == 2
            ? (hotList[0].GetInt32(), hotList[1].GetInt32())
            : (0, 0);
        var item = new CosmeticItem(
            code,
            slot.Value,
            rarity,
            Number(element, "price"),
            !element.TryGetProperty("chest", out var chest) || chest.ValueKind != JsonValueKind.False,
            (int)Number(element, "unlockLevel"),
            element.TryGetProperty("starter", out var starter) && starter.ValueKind == JsonValueKind.True,
            animations,
            (int)Number(element, "fps"),
            hot.Item1,
            hot.Item2,
            colors,
            recolor);

        var problem = Check(item, keys, names);
        if (problem is not null)
        {
            log.Warn($"Косметика {code} пропущена: {problem}");
            return null;
        }

        return item;
    }

    /// <summary>Что не так с предметом или null.</summary>
    internal static string? Check(CosmeticItem item, ISet<char>? keys, ISet<string>? names)
    {
        if (item.Price < 0 || item.UnlockLevel < 0 || item.Fps < 0)
        {
            return "отрицательная цена, уровень или скорость";
        }

        var needsArt = item.Slot is not (CosmeticSlot.Theme or CosmeticSlot.Coin);
        if (needsArt && item.Frames(CosmeticItem.DefaultAnimation).Count == 0)
        {
            return "нет картинки";
        }

        foreach (var frame in item.Animations.Values.SelectMany(frames => frames))
        {
            if (frame.Count == 0 || frame.Any(row => row.Length == 0))
            {
                return "пустой кадр";
            }

            if (keys is not null && frame.Any(row => row.Any(symbol => symbol != '.' && !keys.Contains(symbol))))
            {
                return "символ не из палитры";
            }
        }

        if (item.Slot == CosmeticSlot.Cursor)
        {
            var frame = item.Frames(CosmeticItem.DefaultAnimation)[0];
            if (frame.Count > MaxCursorArt || frame.Max(row => row.Length) > MaxCursorArt)
            {
                return $"курсор больше {MaxCursorArt}×{MaxCursorArt}";
            }

            if (item.HotX < 0 || item.HotY < 0 || item.HotX >= MaxCursorArt || item.HotY >= MaxCursorArt)
            {
                return "горячая точка вне картинки";
            }
        }

        if (item.Slot == CosmeticSlot.Pet && !item.Animations.ContainsKey("idle"))
        {
            return "у питомца нет анимации idle";
        }

        if (item.Slot == CosmeticSlot.Theme)
        {
            if (item.Colors.Count == 0)
            {
                return "у темы нет цветов";
            }

            foreach (var (name, hex) in item.Colors)
            {
                if (names is not null && !names.Contains(name))
                {
                    return $"нет цвета палитры {name}";
                }

                if (hex.Length != 7 || hex[0] != '#' || !hex[1..].All(Uri.IsHexDigit))
                {
                    return $"цвет {name} не в виде #RRGGBB";
                }
            }
        }

        if (item.Slot == CosmeticSlot.Coin)
        {
            if (item.Recolor.Count == 0)
            {
                return "у скина монет нет замены цветов";
            }

            if (keys is not null && item.Recolor.Any(pair => !keys.Contains(pair.Key) || !keys.Contains(pair.Value)))
            {
                return "замена цветов не из палитры";
            }
        }

        return null;
    }

    private static IReadOnlyList<string> Rows(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(row => row.GetString() ?? string.Empty).ToList()
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

    private static CosmeticRarity Fail(ILog log, string message)
    {
        log.Warn(message);
        return CosmeticRarity.Common;
    }
}
