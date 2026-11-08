using ADHDWindows.Core.Events;
using ADHDWindows.Core.Hardships;

namespace ADHDWindows.Modules.Hardships.Basic;

/// <summary>Файлы из папки assets рядом с программой.</summary>
public static class AssetFiles
{
    /// <summary>
    /// Полный путь к файлу или папке assets/<paramref name="relative"/>, null — не найдено.
    /// Программа лежит рядом с assets, а проверки запускаются из папки сборки внутри репозитория, поэтому поиск идёт вверх.
    /// </summary>
    public static string? Find(string relative)
    {
        var path = Path.Combine("assets", relative);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; directory is not null && level < 8; level++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, path);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// Можно ли сейчас шуметь. Усложнения и так снимаются при «Не беспокоить» и созвоне, но звук проверяет это ещё раз
/// перед каждым проигрыванием: между включением режима и снятием усложнения проходит до секунды.
/// </summary>
public sealed class QuietWatch
{
    private volatile bool _quiet;

    public QuietWatch(IEventBus events, TimeProvider? clock = null)
    {
        var time = clock ?? TimeProvider.System;
        events.Subscribe<HardshipStatus>(status =>
            _quiet = status.Blocked is not null || (status.QuietUntil is { } until && until > time.GetUtcNow()));
        events.Publish(new HardshipStatusRequested());
    }

    /// <summary>Включено «Не беспокоить», идёт созвон или усложнения запрещены по другой причине.</summary>
    public bool IsQuiet => _quiet;
}
