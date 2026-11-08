using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Storage;

namespace ADHDWindows.Modules.Locks;

/// <summary>Запись о покупке: дата и цена нужны статистике.</summary>
public sealed record UnlockRecord(string Name, long Price, LockKind Kind, DateTimeOffset When);

/// <summary>Постоянные разблокировки в сохранении.</summary>
public sealed class UnlockLedger(IKeyValueStore saves)
{
    public const string SaveKey = "unlocked";

    private readonly Dictionary<string, UnlockRecord> _records =
        saves.Get<Dictionary<string, UnlockRecord>>(SaveKey) ?? [];

    public bool Contains(string key) => _records.ContainsKey(key);

    public IReadOnlyCollection<string> Keys => _records.Keys;

    public void Add(LockOffer offer, DateTimeOffset when)
    {
        _records[offer.Key] = new UnlockRecord(offer.Name, offer.Price, offer.Kind, when);
        saves.Set(SaveKey, _records);
    }
}
