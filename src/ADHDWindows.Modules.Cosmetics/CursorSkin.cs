using ADHDWindows.Core.Cosmetics;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Pixels;
using ADHDWindows.Core.Safety;
using ADHDWindows.Modules.Hardships.Basic;
using ADHDWindows.Modules.Hardships.Screen;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Cosmetics;

/// <summary>Символы палитры из assets/palette.json: ими нарисована вся косметика.</summary>
public sealed class PaletteArt
{
    private PaletteArt(IReadOnlyDictionary<char, uint> keys, IReadOnlySet<string> names)
    {
        Keys = keys;
        Names = names;
    }

    public IReadOnlyDictionary<char, uint> Keys { get; }

    public IReadOnlySet<string> Names { get; }

    /// <summary>Палитра рядом с программой. Если файла нет — цвета из сборки экранных усложнений, без проверки названий.</summary>
    public static PaletteArt Load(ILog log)
    {
        var path = AssetFiles.Find("palette.json");
        if (path is not null)
        {
            try
            {
                var palette = Palette.Parse(File.ReadAllText(path));
                return new PaletteArt(
                    palette.Colors.ToDictionary(color => color.Key, color => color.Argb),
                    palette.Colors.Select(color => color.Name).ToHashSet(StringComparer.Ordinal));
            }
            catch (Exception exception) when (exception is IOException or FormatException or System.Text.Json.JsonException)
            {
                log.Error("Палитра не прочитана, косметика рисуется запасными цветами", exception);
            }
        }
        else
        {
            log.Warn("Палитра не найдена, косметика рисуется запасными цветами");
        }

        return new PaletteArt(Colors.Keys, new HashSet<string>(StringComparer.Ordinal));
    }

    public Picture Render(IReadOnlyList<string> rows) => Picture.FromRows(rows, Keys);
}

/// <summary>
/// Скин курсора: заменяет стрелку, руку и стрелку с фоновой работой картинкой предмета. Текстовый курсор и стрелки размеров
/// остаются системными. Усложнения вида курсора важнее скина: пока такое идёт, скин не ставится, а после его конца
/// возвращается. Изменение записано в реестр, поэтому после сбоя курсоры откатывает сторож.
/// </summary>
public sealed class CursorSkin : IDisposable
{
    private readonly ISystemChanges _changes;
    private readonly ILog _log;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _pausedBy = new(StringComparer.Ordinal);
    private CosmeticItem? _item;
    private Picture? _picture;
    private IDisposable? _mark;
    private bool _installed;

    public CursorSkin(ISystemChanges changes, ILog log)
    {
        _changes = changes;
        _log = log;
    }

    /// <summary>Скин сейчас стоит в системе.</summary>
    public bool Installed
    {
        get
        {
            lock (_lock)
            {
                return _installed;
            }
        }
    }

    /// <summary>Усложнения, из-за которых скин сейчас снят.</summary>
    public bool Paused
    {
        get
        {
            lock (_lock)
            {
                return _pausedBy.Count > 0;
            }
        }
    }

    /// <summary>Надевает предмет или снимает скин (null).</summary>
    public void Set(CosmeticItem? item, Picture? picture)
    {
        lock (_lock)
        {
            if (item?.Code == _item?.Code)
            {
                return;
            }

            _item = item;
            _picture = picture;
            if (_item is null)
            {
                RemoveLocked();
            }
            else
            {
                InstallLocked();
            }
        }
    }

    /// <summary>Началось усложнение вида курсора: оно само заменяет курсоры, а в конце возвращает схему пользователя.</summary>
    public void Pause(string hardship)
    {
        lock (_lock)
        {
            _pausedBy.Add(hardship);

            // Курсоры теперь у усложнения: восстанавливать их здесь нельзя, иначе снимется и его вид.
            _installed = false;
        }
    }

    /// <summary>Усложнение закончилось и вернуло схему пользователя: скин ставится снова.</summary>
    public void Resume(string hardship)
    {
        lock (_lock)
        {
            if (_pausedBy.Remove(hardship) && _pausedBy.Count == 0)
            {
                InstallLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _item = null;
            RemoveLocked();
        }
    }

    private void InstallLocked()
    {
        if (_item is null || _picture is null || _pausedBy.Count > 0)
        {
            return;
        }

        try
        {
            _mark ??= _changes.Record(CursorSkinRestorer.ChangeKind, string.Empty);
            var size = CursorControl.SystemCursorSize();
            var (pixels, hotX, hotY) = Scale(_picture, _item, size);
            CursorControl.ReplaceCursors(pixels, size, hotX, hotY, CursorControl.PointerCursors);
            _installed = true;
            _log.Info($"Скин курсора поставлен: {_item.Code}");
        }
        catch (Exception exception)
        {
            _log.Error($"Скин курсора не поставлен: {_item.Code}", exception);
            RemoveLocked();
        }
    }

    private void RemoveLocked()
    {
        if (_installed)
        {
            try
            {
                CursorControl.RestoreCursors();
                _log.Info("Скин курсора снят");
            }
            catch (Exception exception)
            {
                // Запись в реестре остаётся: курсоры вернёт программа при следующем запуске или сторож.
                _log.Error("Курсоры не вернулись", exception);
                _installed = false;
                return;
            }

            _installed = false;
        }

        // Пока идёт усложнение, его курсоры вернёт оно само; запись скина больше не нужна.
        _mark?.Dispose();
        _mark = null;
    }

    /// <summary>Картинка с целым увеличением на холсте курсора и горячая точка в пикселях холста.</summary>
    internal static (uint[] Pixels, int HotX, int HotY) Scale(Picture picture, CosmeticItem item, int size)
    {
        var scale = Math.Max(1, size / Math.Max(CosmeticCatalog.MaxCursorArt, Math.Max(picture.Width, picture.Height)));
        var pixels = new uint[size * size];
        for (var y = 0; y < picture.Height * scale && y < size; y++)
        {
            for (var x = 0; x < picture.Width * scale && x < size; x++)
            {
                pixels[(y * size) + x] = picture[x / scale, y / scale];
            }
        }

        return (pixels, Math.Min(size - 1, item.HotX * scale), Math.Min(size - 1, item.HotY * scale));
    }
}

/// <summary>Возвращает курсоры из схемы пользователя после скина.</summary>
public sealed class CursorSkinRestorer : ISystemRestorer
{
    public const string ChangeKind = "cosmetics.cursor";

    public string Kind => ChangeKind;

    public void Restore(string data) => CursorControl.RestoreCursors();
}
