using System.Text.Json;
using ADHDWindows.Core.Logging;
using Microsoft.Win32;

namespace ADHDWindows.Modules.Locks.Games;

/// <summary>Steam: libraryfolders.vdf перечисляет библиотеки, appmanifest_*.acf описывает каждую игру.</summary>
/// <param name="steamPath">Папка Steam. Если не задана, берётся из реестра.</param>
public sealed class SteamSource(string? steamPath, ILog log) : IGameSource
{
    public string Launcher => "steam";

    public IReadOnlyList<InstalledGame> Read()
    {
        var games = new List<InstalledGame>();
        foreach (var library in Libraries())
        {
            var apps = Path.Combine(library, "steamapps");
            foreach (var manifest in SafeFiles(apps, "appmanifest_*.acf"))
            {
                if (ReadManifest(manifest, apps) is { } game)
                {
                    games.Add(game);
                }
            }
        }

        return games;
    }

    public IReadOnlyList<string> LibraryRoots() =>
        [.. Libraries().Select(library => Path.Combine(library, "steamapps", "common"))];

    /// <summary>Папки библиотек. Основная папка Steam — тоже библиотека, даже если файла со списком нет.</summary>
    private List<string> Libraries()
    {
        var root = steamPath ?? FindSteam();
        if (root is null || !Directory.Exists(root))
        {
            return [];
        }

        var libraries = new List<string> { Path.GetFullPath(root) };
        var list = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(list))
            {
                var folders = Vdf.Parse(File.ReadAllText(list))["libraryfolders"];
                foreach (var entry in folders?.Children.Values ?? Enumerable.Empty<VdfNode>())
                {
                    // Старый формат: "1" "D:\\Games", новый: "1" { "path" "D:\\Games" }.
                    var path = entry.Value ?? entry.Text("path");
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    {
                        libraries.Add(Path.GetFullPath(path));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            log.Warn($"Не удалось прочитать список библиотек Steam: {exception.Message}");
        }

        return [.. libraries.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private InstalledGame? ReadManifest(string manifest, string apps)
    {
        try
        {
            var state = Vdf.Parse(File.ReadAllText(manifest))["AppState"];
            var id = state?.Text("appid");
            var folder = state?.Text("installdir");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            var size = long.TryParse(state!.Text("SizeOnDisk"), out var bytes) && bytes > 0 ? bytes : (long?)null;
            return new InstalledGame(
                Launcher,
                id,
                state.Text("name") ?? folder,
                Path.Combine(apps, "common", folder),
                size);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            log.Warn($"Не удалось прочитать {manifest}: {exception.Message}");
            return null;
        }
    }

    private static string? FindSteam() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
        ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;

    internal static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Epic Games: каждая установка описана файлом *.item в папке Manifests.</summary>
/// <param name="manifests">Папка с файлами *.item. Если не задана, берётся стандартная.</param>
public sealed class EpicSource(string? manifests, ILog log) : IGameSource
{
    private static readonly string DefaultManifests = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public string Launcher => "epic";

    public IReadOnlyList<InstalledGame> Read()
    {
        var games = new List<InstalledGame>();
        foreach (var file in SteamSource.SafeFiles(manifests ?? DefaultManifests, "*.item"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (ToGame(document.RootElement) is { } game)
                {
                    games.Add(game);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                log.Warn($"Не удалось прочитать {file}: {exception.Message}");
            }
        }

        return games;
    }

    public IReadOnlyList<string> LibraryRoots() => [];

    private InstalledGame? ToGame(JsonElement item)
    {
        var id = Text(item, "AppName");
        var folder = Text(item, "InstallLocation");
        if (id is null || folder is null)
        {
            return null;
        }

        // Дополнение лежит в папке основной игры, отдельной записи ему не нужно.
        if (Text(item, "MainGameAppName") is { } main && !main.Equals(id, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // У программ вроде Unreal Engine нет категории games.
        if (item.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array
            && !categories.EnumerateArray().Any(category => category.GetString() == "games"))
        {
            return null;
        }

        var size = item.TryGetProperty("InstallSize", out var bytes) && bytes.TryGetInt64(out var value) && value > 0 ? value : (long?)null;
        return new InstalledGame(Launcher, id, Text(item, "DisplayName") ?? id, folder, size);
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}

/// <summary>
/// Лаунчеры, которые записывают игры в реестр: раздел на игру, в нём папка и название.
/// Так устроены GOG и Ubisoft Connect.
/// </summary>
/// <param name="keyPath">Раздел внутри HKLM.</param>
/// <param name="folderValue">Значение с папкой игры.</param>
/// <param name="nameValue">Значение с названием. Если его нет, названием служит имя папки.</param>
public sealed class RegistrySource(string launcher, string keyPath, string folderValue, string? nameValue, ILog log) : IGameSource
{
    public string Launcher => launcher;

    public static RegistrySource Gog(ILog log) =>
        new("gog", @"SOFTWARE\WOW6432Node\GOG.com\Games", "path", "gameName", log);

    public static RegistrySource Ubisoft(ILog log) =>
        new("ubisoft", @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", "InstallDir", null, log);

    public IReadOnlyList<InstalledGame> Read()
    {
        var games = new List<InstalledGame>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(keyPath);
            foreach (var id in root?.GetSubKeyNames() ?? [])
            {
                using var key = root!.OpenSubKey(id);
                if (key?.GetValue(folderValue) is not string folder || string.IsNullOrWhiteSpace(folder))
                {
                    continue;
                }

                folder = folder.Replace('/', '\\').TrimEnd('\\');
                var name = nameValue is null ? null : key.GetValue(nameValue) as string;
                games.Add(new InstalledGame(launcher, id, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(folder) : name, folder));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Warn($"Не удалось прочитать игры {launcher}: {exception.Message}");
        }

        return games;
    }

    public IReadOnlyList<string> LibraryRoots() => [];
}
