namespace ADHDWindows.Core.Hosting;

/// <summary>Расположение файлов: установленные рядом с программой и личные данные пользователя.</summary>
public sealed record AppPaths(string InstallDir, string UserDir)
{
    public string DataDir => Path.Combine(InstallDir, "data");
    public string AssetsDir => Path.Combine(InstallDir, "assets");
    public string ModulesDir =>Path.Combine(InstallDir, "modules");
    public string SavePath => Path.Combine(UserDir, "save.json");
    public string SettingsPath => Path.Combine(UserDir, "settings.json");
    public string LogsDir => Path.Combine(UserDir, "logs");

    public static AppPaths ForCurrentUser() => new(
        AppContext.BaseDirectory,
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ADHDWindows"));
}
