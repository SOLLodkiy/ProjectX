using System.Text.Encodings.Web;
using System.Text.Json;

namespace ADHDWindows.Core.Storage;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Кириллица остаётся читаемой в файлах пользователя.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
