using System.Reflection;
using System.Runtime.Loader;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Core.Modules;

/// <summary>Находит модули в папках modules/&lt;Имя&gt;/&lt;Имя&gt;.dll.</summary>
public static class ModuleLoader
{
    public static IReadOnlyList<IModule> Load(string modulesDirectory, ILog log)
    {
        var modules = new List<IModule>();
        if (!Directory.Exists(modulesDirectory))
        {
            log.Info($"Папка модулей не найдена: {modulesDirectory}");
            return modules;
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
                modules.AddRange(CreateModules(assemblyPath));
            }
            catch (Exception ex)
            {
                log.Error($"Модуль не загружен: {assemblyPath}", ex);
            }
        }

        return modules;
    }

    private static List<IModule> CreateModules(string assemblyPath)
    {
        var context = new ModuleLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);
        return assembly.GetExportedTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IModule).IsAssignableFrom(type))
            .Select(type => (IModule)Activator.CreateInstance(type)!)
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
