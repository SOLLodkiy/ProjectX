using System.Reflection;
using System.Runtime.Loader;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Safety;

namespace ADHDWindows.Core.Modules;

/// <summary>Находит модули в папках modules/&lt;Имя&gt;/&lt;Имя&gt;.dll.</summary>
public static class ModuleLoader
{
    public static IReadOnlyList<IModule> Load(string modulesDirectory, ILog log) =>
        LoadExports<IModule>(modulesDirectory, log);

    /// <summary>Восстановители из сборок модулей. Сторожевой процесс получает их так же, как программа.</summary>
    public static IReadOnlyList<ISystemRestorer> LoadRestorers(string modulesDirectory, ILog log) =>
        LoadExports<ISystemRestorer>(modulesDirectory, log);

    private static List<T> LoadExports<T>(string modulesDirectory, ILog log)
        where T : class
    {
        var exports = new List<T>();
        if (!Directory.Exists(modulesDirectory))
        {
            log.Info($"Папка модулей не найдена: {modulesDirectory}");
            return exports;
        }

        foreach (var directory in Directory.GetDirectories(modulesDirectory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            var assemblyPath = Path.Combine(directory, $"{name}.dll");
            if (!File.Exists(assemblyPath))
            {
                log.Warn($"В папке модуля нет {name}.dll: {directory}");
                continue;
            }

            try
            {
                exports.AddRange(CreateExports<T>(assemblyPath));
            }
            catch (Exception ex)
            {
                log.Error($"Модуль не загружен: {assemblyPath}", ex);
            }
        }

        return exports;
    }

    private static List<T> CreateExports<T>(string assemblyPath)
        where T : class
    {
        var context = new ModuleLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);
        return assembly.GetExportedTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(T).IsAssignableFrom(type))
            .Select(type => (T)Activator.CreateInstance(type)!)
            .ToList();
    }

    /// <summary>
    /// Даёт модулю собственные зависимости, а сборку ядра берёт у программы:
    /// иначе IModule из модуля и из ядра были бы разными типами.
    /// </summary>
    private sealed class ModuleLoadContext(string assemblyPath)
        : AssemblyLoadContext(Path.GetFileNameWithoutExtension(assemblyPath))
    {
        private static readonly string HostAssemblyName = typeof(IModule).Assembly.GetName().Name!;

        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == HostAssemblyName)
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
