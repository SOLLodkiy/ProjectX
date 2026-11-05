using System.Globalization;

namespace ADHDWindows.Core.Safety;

/// <summary>Командная строка сторожевого процесса.</summary>
/// <param name="ProcessStarted">Время запуска программы: номер процесса система может выдать другому.</param>
public sealed record WatchdogArguments(
    int ProcessId,
    long ProcessStarted,
    Guid Session,
    string InstallDir,
    string UserDir,
    string Hotkey)
{
    private const int Count = 6;

    public IReadOnlyList<string> ToList() =>
    [
        ProcessId.ToString(CultureInfo.InvariantCulture),
        ProcessStarted.ToString(CultureInfo.InvariantCulture),
        Session.ToString("N"),
        InstallDir,
        UserDir,
        Hotkey,
    ];

    public static WatchdogArguments? Parse(IReadOnlyList<string> args)
    {
        if (args.Count != Count
            || !int.TryParse(args[0], CultureInfo.InvariantCulture, out var processId)
            || !long.TryParse(args[1], CultureInfo.InvariantCulture, out var started)
            || !Guid.TryParse(args[2], out var session))
        {
            return null;
        }

        return new WatchdogArguments(processId, started, session, args[3], args[4], args[5]);
    }
}
