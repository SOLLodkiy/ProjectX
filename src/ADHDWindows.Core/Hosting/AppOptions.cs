namespace ADHDWindows.Core.Hosting;

/// <summary>Параметры запуска из командной строки.</summary>
public sealed record AppOptions(bool DeveloperMode)
{
    public const string DeveloperFlag = "--dev";
    public const string ShowcaseFlag = "--showcase";

    /// <summary>Открыть витрину компонентов при запуске. Работает только в режиме разработчика.</summary>
    public bool OpenShowcase { get; init; }

    public static AppOptions Parse(IEnumerable<string> args)
    {
        var list = args.ToList();
        var developerMode = list.Contains(DeveloperFlag, StringComparer.OrdinalIgnoreCase);
        return new AppOptions(developerMode)
        {
            OpenShowcase = developerMode && list.Contains(ShowcaseFlag, StringComparer.OrdinalIgnoreCase),
        };
    }
}
