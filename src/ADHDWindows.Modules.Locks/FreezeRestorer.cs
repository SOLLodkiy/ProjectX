using System.Text.Json;
using ADHDWindows.Core.Safety;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Locks;

/// <summary>Что заморожено одной записью реестра изменений.</summary>
public sealed record FreezeRecord(IReadOnlyList<ProcessStamp> Processes, IReadOnlyList<long> Windows)
{
    public string ToData() => JsonSerializer.Serialize(this);

    public static FreezeRecord? Parse(string data) => JsonSerializer.Deserialize<FreezeRecord>(data);
}

/// <summary>Размораживает программы после сбоя. Работает и в стороже, где модуля нет.</summary>
public sealed class FreezeRestorer : ISystemRestorer
{
    public const string ChangeKind = "locks.freeze";

    public string Kind => ChangeKind;

    public void Restore(string data)
    {
        if (FreezeRecord.Parse(data) is not { } record)
        {
            return;
        }

        foreach (var stamp in record.Processes)
        {
            ProcessControl.Thaw(stamp);
        }

        foreach (var value in record.Windows)
        {
            var window = new IntPtr(value);
            if (record.Processes.Any(stamp => WindowControl.BelongsTo(window, stamp.Id)))
            {
                WindowControl.RestoreWindow(window);
            }
        }
    }
}
