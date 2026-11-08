using System.Text.Json;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Core.Stats;

/// <summary>Экспорт статистики в файл, который игрок сохраняет сам. Программа никуда его не отправляет.</summary>
public static class StatsExport
{
    public static string ToJson(StatsSnapshot snapshot) => JsonSerializer.Serialize(snapshot, JsonDefaults.Options);

    /// <summary>Записывает файл целиком или не трогает прежний: сначала во временный файл, затем замена.</summary>
    public static void Write(StatsSnapshot snapshot, string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson(snapshot), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    public static string DefaultFileName(DateTimeOffset when) => $"ADHDWindows-stats-{when.LocalDateTime:yyyyMMdd-HHmm}.json";
}
