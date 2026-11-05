namespace ADHDWindows.Core.Hosting;

/// <summary>Параметры запуска из командной строки.</summary>
public sealed record AppOptions(bool DeveloperMode)
{
    public const string DeveloperFlag = "--dev";

    public static AppOptions Parse(IEnumerable<string> args) =>
        new(args.Contains(DeveloperFlag, StringComparer.OrdinalIgnoreCase));
}
