using System.Text.Json;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Data;

/// <summary>
/// Контент из папки data. Ключ файла — путь без расширения:
/// data/texts/ru.json читается по ключу "texts/ru".
/// </summary>
public sealed class GameData(string directory, ILog log)
{
    private Dictionary<string, JsonElement> _files = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Keys => _files.Keys;

    /// <summary>Читает все файлы. Файл с ошибкой пропускается и не мешает остальным.</summary>
    public void Load()
    {
        var files = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
        {
            log.Warn($"Папка данных не найдена: {directory}");
            _files = files;
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.OpenRead(file);
                files[ToKey(file)] = JsonSerializer.Deserialize<JsonElement>(stream, JsonDefaults.Options);
            }
            catch (JsonException ex)
            {
                log.Error($"Файл данных пропущен: {file}", ex);
            }
        }

        _files = files;
    }

    public bool TryGet<T>(string key, out T? value)
    {
        value = default;
        if (!_files.TryGetValue(key, out var element))
        {
            return false;
        }

        try
        {
            value = element.Deserialize<T>(JsonDefaults.Options);
            return true;
        }
        catch (JsonException ex)
        {
            log.Error($"Данные {key} имеют неверный формат", ex);
            return false;
        }
    }

    private string ToKey(string file) =>
        Path.ChangeExtension(Path.GetRelativePath(directory, file), null).Replace('\\', '/');
}
