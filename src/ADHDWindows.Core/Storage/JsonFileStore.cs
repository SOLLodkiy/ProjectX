using System.Text.Json;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Core.Storage;

/// <summary>
/// Хранилище в JSON-файле. Запись атомарна и оставляет копию .bak,
/// поэтому сбой во время сохранения не теряет прошлые данные.
/// </summary>
public sealed class JsonFileStore(string path, ILog log) : IKeyValueStore
{
    private const int SchemaVersion = 1;

    private readonly Lock _lock = new();
    private Dictionary<string, JsonElement> _values = [];
    private bool _dirty;

    public string Path { get; } = path;

    private string BackupPath => Path + ".bak";

    /// <summary>Читает файл. Повреждённый файл сохраняется как .corrupt-*, а данные берутся из .bak.</summary>
    public void Load()
    {
        lock (_lock)
        {
            if (TryRead(Path, out var values))
            {
                _values = values;
                _dirty = false;
                return;
            }

            if (File.Exists(Path))
            {
                var quarantine = $"{Path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
                File.Copy(Path, quarantine);
                log.Warn($"Файл {Path} повреждён, копия: {quarantine}");
            }

            if (TryRead(BackupPath, out values))
            {
                log.Warn($"Данные восстановлены из {BackupPath}");
                _values = values;
                _dirty = true;
                return;
            }

            _values = [];
            _dirty = false;
        }
    }

    public T? Get<T>(string key, T? fallback = default)
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(key, out var element))
            {
                return fallback;
            }

            try
            {
                return element.Deserialize<T>(JsonDefaults.Options);
            }
            catch (JsonException ex)
            {
                log.Error($"Значение {key} в {Path} имеет неверный формат", ex);
                return fallback;
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        var element = JsonSerializer.SerializeToElement(value, JsonDefaults.Options);
        lock (_lock)
        {
            _values[key] = element;
            _dirty = true;
        }
    }

    public bool Remove(string key)
    {
        lock (_lock)
        {
            var removed = _values.Remove(key);
            _dirty |= removed;
            return removed;
        }
    }

    /// <summary>Удаляет все значения. На диск изменения попадут при следующей записи.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _dirty |= _values.Count > 0;
            _values = [];
        }
    }

    /// <summary>Записывает изменения на диск. При ошибке изменения остаются и попадут в следующую запись.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, new FileContent(SchemaVersion, _values), JsonDefaults.Options);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(Path))
            {
                File.Replace(temp, Path, BackupPath);
            }
            else
            {
                File.Move(temp, Path);
            }

            _dirty = false;
        }
    }

    private bool TryRead(string file, out Dictionary<string, JsonElement> values)
    {
        values = [];
        if (!File.Exists(file))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(file);
            var content = JsonSerializer.Deserialize<FileContent>(stream, JsonDefaults.Options);
            if (content?.Values is null)
            {
                return false;
            }

            values = content.Values;
            return true;
        }
        catch (JsonException ex)
        {
            log.Error($"Не удалось разобрать {file}", ex);
            return false;
        }
    }

    private sealed record FileContent(int SchemaVersion, Dictionary<string, JsonElement> Values);
}
