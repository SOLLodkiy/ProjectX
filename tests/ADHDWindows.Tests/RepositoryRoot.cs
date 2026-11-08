namespace ADHDWindows.Tests;

internal static class RepositoryRoot
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    public static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Корень репозитория не найден");
    }

    /// <summary>Копирует собранный тестовый модуль в папку modules, как при установке.</summary>
    public static void DeploySampleModule(string modulesDirectory) =>
        DeployModule("ADHDWindows.Modules.Sample", modulesDirectory);

    /// <summary>Копирует собранный модуль в папку modules, как при установке.</summary>
    public static void DeployModule(string name, string modulesDirectory)
    {
        // Модулю, который работает с Windows напрямую, нужна цель net10.0-windows.
        var bin = Path.Combine(Find(), "src", name, "bin", Configuration);
        var output = Directory.Exists(Path.Combine(bin, "net10.0")) ? Path.Combine(bin, "net10.0") : Path.Combine(bin, "net10.0-windows");
        var target = Path.Combine(modulesDirectory, name);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(output))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }
}
