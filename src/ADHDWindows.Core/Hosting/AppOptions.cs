namespace ADHDWindows.Core.Hosting;

/// <summary>Параметры запуска из командной строки.</summary>
public sealed record AppOptions(bool DeveloperMode)
{
    public const string DeveloperFlag = "--dev";
    public const string ShowcaseFlag = "--showcase";
    public const string UserDirPrefix = "--user-dir=";
    public const string RecoveryProbePrefix = "--recovery-probe=";
    public const string HardshipPrefix = "--hardship=";

    /// <summary>Открыть витрину компонентов при запуске. Работает только в режиме разработчика.</summary>
    public bool OpenShowcase { get; init; }

    /// <summary>Папка данных вместо обычной, чтобы проверки не трогали сохранение пользователя. Только в режиме разработчика.</summary>
    public string? UserDir { get; init; }

    /// <summary>Сделать тестовое изменение системы после запуска. Только в режиме разработчика.</summary>
    public string? RecoveryProbe { get; init; }

    /// <summary>Запустить усложнение с этим кодом сразу после запуска, «random» — случайное. Только в режиме разработчика.</summary>
    public string? Hardship { get; init; }

    public static AppOptions Parse(IEnumerable<string> args)
    {
        var list = args.ToList();
        var developerMode = list.Contains(DeveloperFlag, StringComparer.OrdinalIgnoreCase);
        if (!developerMode)
        {
            return new AppOptions(false);
        }

        return new AppOptions(true)
        {
            OpenShowcase = list.Contains(ShowcaseFlag, StringComparer.OrdinalIgnoreCase),
            UserDir = Value(list, UserDirPrefix),
            RecoveryProbe = Value(list, RecoveryProbePrefix),
            Hardship = Value(list, HardshipPrefix),
        };
    }

    private static string? Value(List<string> args, string prefix)
    {
        var argument = args.Find(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return argument is null || argument.Length == prefix.Length ? null : argument[prefix.Length..];
    }
}
