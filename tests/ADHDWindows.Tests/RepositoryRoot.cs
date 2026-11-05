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
        var output = Path.Combine(Find(), "src", name, "bin", Configuration, "net10.0");
        var target = Path.Combine(modulesDirectory, name);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(output))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }
}
